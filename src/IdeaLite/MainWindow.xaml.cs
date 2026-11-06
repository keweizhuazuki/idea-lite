using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using IdeaLite.Core;
using Microsoft.Win32;

namespace IdeaLite;

public partial class MainWindow : Window
{
    public static TextEditor? ActiveEditor { get; private set; }
    private WorkspaceSnapshot? _workspace;
    private GitService? _git;
    private GitStatus? _gitStatus;
    private CancellationTokenSource? _workspaceLoad;
    private CancellationTokenSource? _diffLoad;
    private CancellationTokenSource? _command;
    private bool _commandBusy, _loadingDocument, _refreshingGit;
    private string? _outputMessage;
    private readonly List<DocumentState> _documents = [];
    private readonly Stack<(string Path, int Line)> _back = new();
    private DocumentState? _active;
    private DateTime _lastShift, _lastRefresh;
    private bool _shiftOnly;
    private readonly DispatcherTimer _memoryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private int[] _diffLines = [];
    private int _diffIndex = -1;
    private DiffScrollSynchronizer _diffScroll = null!;
    private bool _focusMode;
    private int _openRequest;
    private readonly string? _initialRoot;
    private sealed record FileNode(string Path, bool IsFile);
    private sealed class DocumentState(OpenedFile file)
    {
        public OpenedFile File { get; set; } = file;
        public TextDocument Document { get; } = new(file.Text);
        public bool Dirty { get; set; }
        public int Caret { get; set; }
        public double Scroll { get; set; }
        public bool Saving { get; set; }
    }

    public MainWindow(string? initialRoot)
    {
        _initialRoot = initialRoot;
        InitializeComponent();
        ActiveEditor = Editor;
        EditorSupport.Configure(Editor, false);
        foreach (var editor in new[] { OldEditor, NewEditor, OutputEditor }) EditorSupport.Configure(editor);
        OutputEditor.FontSize = 12;
        OutputEditor.ShowLineNumbers = false;
        OutputEditor.Options.AllowScrollBelowDocument = false;
        OutputEditor.TextArea.TextView.LineTransformers.Add(new BuildOutputColorizer());
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaret();
        Editor.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                Editor.FontSize = Math.Clamp(Editor.FontSize + (e.Delta > 0 ? 1 : -1), 10, 26);
                e.Handled = true;
            }
        };
        _diffScroll = new DiffScrollSynchronizer(OldEditor, NewEditor);
        Editor.IsReadOnly = true;
        InitializeWorkbench();
        _layout.Language = UiText.Current.Language;
        UiText.Current.PropertyChanged += LanguageChanged;
        Closed += (_, _) => UiText.Current.PropertyChanged -= LanguageChanged;
        _outputTimer.Tick += (_, _) => FlushBuildOutput();

        _memoryTimer.Tick += (_, _) => UpdateMemory(); _memoryTimer.Start(); UpdateMemory();
        Loaded += async (_, _) => { if (_initialRoot != null) await Guard(() => OpenWorkspaceAsync(_initialRoot)); };
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(ex.Message); Dialogs.Error(this, ex); }
    }
    private void SetStatus(string text) => UiText.Set(StatusLabel, "Text", text.Replace('\n', ' ').Replace('\r', ' '));
    private void SetStatus(Func<string> text) => UiText.Set(StatusLabel, "Text", text);
    private void UpdateMemory()
    {
        using var process = Process.GetCurrentProcess();
        MemoryLabel.Text = $"{process.WorkingSet64 / 1048576.0:F0} MiB";
        UiText.Set(MemoryLabel, "ToolTip", $"当前进程工作集；专用内存 {process.PrivateMemorySize64 / 1048576.0:F0} MB。Git/Maven 子进程另计。");
    }
    private void UpdateCaret()
    {
        var style = _active?.File.Style;
        CaretLabel.Text = $"Ln {Editor.TextArea.Caret.Line}, Col {Editor.TextArea.Caret.Column}" + (style == null ? "" : $"   {(style.UseTabs ? "Tab" : "Spaces")} {style.IndentSize}   {(style.NewLine == "\r\n" ? "CRLF" : "LF")}   {_active!.File.Encoding.WebName}");
    }

    private async Task OpenWorkspaceAsync(string root)
    {
        if (_commandBusy) { SetStatus("请等待正在运行的命令结束，或先停止命令。"); return; }
        if (!ConfirmCloseAll()) return;
        _workspaceLoad?.Cancel(); _workspaceLoad?.Dispose(); _workspaceLoad = new();
        ++_openRequest;
        ResetReviewState();
        var token = _workspaceLoad.Token;
        _documents.Clear(); _active = null; _back.Clear(); _workspace = null; _git = null; _gitStatus = null;
        _loadingDocument = true; Editor.Document = new TextDocument(); Editor.IsReadOnly = true; _loadingDocument = false;
        RebuildTabs(); FileTree.Items.Clear(); ChangesList.ItemsSource = null; MavenList.Items.Clear(); SpringList.ItemsSource = null;
        OldEditor.Clear(); NewEditor.Clear(); CommitMessage.Clear(); AmendCheck.IsChecked = false;
        SetSurface(false);
        root = Path.GetFullPath(root);
        SetStatus("正在打开项目与建立有上限的源码索引…");
        string? gitRoot;
        string? gitIssue = null;
        try { gitRoot = await GitService.FindRootAsync(root, token); }
        catch (Win32Exception) { gitRoot = null; gitIssue = "未找到 Git 可执行文件。请安装 Git for Windows，或将 Git 加入 PATH。"; }
        catch (InvalidOperationException ex) { gitRoot = null; gitIssue = ex.Message; }
        root = gitRoot ?? root;
        var git = gitRoot == null ? null : new GitService(root);
        var snapshot = await WorkspaceService.LoadAsync(root, git, token);
        token.ThrowIfCancellationRequested();
        _workspace = snapshot; _git = git;
        _lastMavenModule = null;
        _layout.MavenGoals = _layout.MavenGoalsByProject.GetValueOrDefault(root, "test");
        RunConfigLabel.Text = "Maven · " + _layout.MavenGoals;
        ProjectTitle.Text = Path.GetFileName(root); ProjectTitle.ToolTip = root;
        Title = $"{Path.GetFileName(root)} — Idea Lite";
        ProjectMark.Text = ProjectTitle.Text.Length > 0 ? ProjectTitle.Text[..1].ToUpperInvariant() : "IL";
        RenderProjectTree();
        PopulateMavenTree(); SpringList.ItemsSource = snapshot.Spring;
        RunConfigButton.IsEnabled = RunButton.IsEnabled = snapshot.Maven.Count > 0;
        UiText.Set(BranchLabel, "Text", git == null ? gitIssue == null ? "Git" : "Git 不可用" : "…");
        BranchButton.ToolTip = gitIssue ?? GitService.ExecutablePath;
        UiText.Set(GitSummary, "Text", git == null ? gitIssue ?? "当前目录没有 Git 仓库" : "");
        GitSummary.ToolTip = gitIssue;
        GitSummary.TextWrapping = TextWrapping.Wrap;
        if (git != null) await RefreshGitAsync();
        if (_bottom == BottomTool.Log) await LoadHistoryAsync();

        var first = snapshot.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("README.md", StringComparison.OrdinalIgnoreCase)) ?? snapshot.Files.FirstOrDefault(p => p.EndsWith("pom.xml"));
        if (first != null) await OpenFileAsync(first);
        SetStatus(gitIssue ?? $"已打开 {root} · {snapshot.Files.Length:N0} 个文件 · {snapshot.Symbols.Count:N0} 个符号" + (snapshot.Limited ? " · 索引达到上限" : ""));
    }

    private void PopulateTree(ItemsControl parent, string prefix)
    {
        if (_workspace == null) return;
        parent.Items.Clear();
        var groups = _workspace.Files.Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
            .GroupBy(p => p[prefix.Length..].Split('/')[0])
            .OrderBy(group => group.Any(path => path == prefix + group.Key)).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var path = prefix + group.Key;
            var isFile = group.Any(p => p == path);
            var name = group.Key;
            if (!isFile)
            {
                while (IsPackageDirectory(path))
                {
                    var tails = group.Select(p => p[(path.Length + 1)..]).ToArray();
                    if (tails.Any(t => !t.Contains('/'))) break;
                    var heads = tails.Select(t => t.Split('/')[0]).Distinct().ToArray();
                    if (heads.Length != 1) break;
                    path += "/" + heads[0]; name += "." + heads[0];
                }
            }
            bool sourceRoot = path.EndsWith("/java", StringComparison.Ordinal) || path.EndsWith("/kotlin", StringComparison.Ordinal);
            bool module = _workspace.Maven.Any(maven => Path.GetDirectoryName(maven.Path)?.Replace('\\', '/') == path);
            var icon = isFile ? FileIcon(path) : sourceRoot ? "SourceRoot" : IsPackageDirectory(path) ? "Package" : module ? "Module" : "Folder";
            var item = new TreeViewItem { Header = FileHeader(name, icon, path), Tag = new FileNode(path, isFile), Style = (Style)FindResource("ProjectTreeItem") };

            if (!isFile)
            {
                item.Items.Add(new TreeViewItem { Header = "…" });
                item.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, item) && item.Items.Count == 1 && ((TreeViewItem)item.Items[0]).Tag == null) PopulateTree(item, path + "/"); };
            }
            parent.Items.Add(item);
        }
    }
    internal static string FileIcon(string path) => Path.GetFileName(path).Equals("pom.xml", StringComparison.OrdinalIgnoreCase) ? "Maven" : Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".java" or ".kt" or ".cs" => "Class",
        ".xml" or ".xaml" or ".html" => "Xml",
        _ => "File"
    };

    private static bool IsPackageDirectory(string path) => path.Contains("/java/", StringComparison.Ordinal) || path.Contains("/kotlin/", StringComparison.Ordinal);

    private async Task OpenFileAsync(string relative, int? line = null, bool remember = true)
    {
        if (_workspace == null) return;
        var request = ++_openRequest;
        _diffLoad?.Cancel();
        var path = FileService.SafePath(_workspace.Root, relative);
        var document = _documents.FirstOrDefault(d => d.File.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (document == null)
        {
            if (_documents.Count >= 12)
            {
                var disposable = _documents.FirstOrDefault(d => !d.Dirty && d != _active);
                if (disposable == null) { SetStatus("最多保留 12 个文档，请先保存或关闭部分标签。"); return; }
                _documents.Remove(disposable);
            }
            var file = await FileService.ReadAsync(path);
            if (request != _openRequest) return;
            document = new(file);
            _documents.Add(document);
            var captured = document;
            captured.Document.Changed += (_, _) => { if (!_loadingDocument) { if (!captured.Dirty) { captured.Dirty = true; RebuildTabs(); } } };
        }
        if (_active != null)
        {
            _active.Caret = Editor.CaretOffset; _active.Scroll = Editor.VerticalOffset;
            if (remember && (_active != document || (line.HasValue && Editor.TextArea.Caret.Line != line.Value))) _back.Push((Relative(_active.File.Path), Editor.TextArea.Caret.Line));
        }
        _loadingDocument = true; _active = document;
        Editor.Document = document.Document; Editor.IsReadOnly = false;
        Editor.Options.ConvertTabsToSpaces = !document.File.Style.UseTabs;
        Editor.Options.IndentationSize = document.File.Style.IndentSize;
        EditorSupport.Highlight(Editor, path);
        Editor.CaretOffset = Math.Min(document.Caret, document.Document.TextLength);
        Editor.ScrollToVerticalOffset(document.Scroll);
        _loadingDocument = false;
        SetSurface(false);
        UpdateStructure();

        if (line is int targetLine)
        {
            targetLine = Math.Clamp(targetLine, 1, document.Document.LineCount);
            Editor.CaretOffset = document.Document.GetLineByNumber(targetLine).Offset;
            Editor.ScrollToLine(targetLine);
        }
        RebuildTabs(); UpdateCaret(); Editor.TextArea.Focus();
        SetStatus(document.File.Style.Source + "");
    }
    private string Relative(string path) => _workspace == null ? path : Path.GetRelativePath(_workspace.Root, path).Replace('\\', '/');
    private void RebuildTabs()
    {
        if (DocumentTabs == null) return;
        DocumentTabs.Children.Clear();
        foreach (var doc in _documents)
        {
            AddEditorTab(Path.GetFileName(doc.File.Path), FileIcon(doc.File.Path), doc.File.Path,
                !_showDiff && doc == _active, doc.Dirty, () => OpenFileAsync(Relative(doc.File.Path)),
                () => CloseDocumentAsync(doc));
        }
        if (_preview != null)
        {
            var preview = _preview;
            AddEditorTab(Path.GetFileName(preview.Path), "Diff", preview.Description, _showDiff, false,
                () => ShowPreviewAsync(preview), () => { ClosePreview(); return Task.CompletedTask; });
        }
    }

    private async Task SaveAsync(bool overwrite = false)
    {
        var doc = _active;
        if (doc == null || !doc.Dirty || doc.Saving) return;
        doc.Saving = true;
        var textToSave = doc.Document.Text;
        try
        {
            var saved = await FileService.SaveAsync(doc.File, textToSave, overwrite);
            doc.File = saved;
            var editedDuringSave = doc.Document.Text != textToSave;
            if (!editedDuringSave && doc.Document.Text != saved.Text)
            {
                _loadingDocument = true;
                var offset = Editor.CaretOffset;
                doc.Document.Replace(0, doc.Document.TextLength, saved.Text);
                if (_active == doc) Editor.CaretOffset = Math.Min(offset, doc.Document.TextLength);
                _loadingDocument = false;
            }
            doc.Dirty = editedDuringSave; RebuildTabs(); UpdateCaret(); SetStatus("已保存 " + Relative(doc.File.Path) + (editedDuringSave ? " · 保存期间的新编辑已保留，请再次保存" : ""));
            await RefreshGitAsync();
        }
        catch (FileChangedException)
        {
            doc.Saving = false;
            if (_active == doc && Dialogs.Confirm(this, "文件已被外部程序更改或删除。是否用编辑器内容覆盖磁盘版本？\n选择“否”会保留编辑器内容，你可以另行复制。")) await SaveAsync(true);
        }
        finally { doc.Saving = false; }
    }
    private bool ConfirmCloseAll() => !_documents.Any(d => d.Dirty) || Dialogs.Confirm(this, "存在未保存的文档。继续会丢弃这些编辑，是否继续？");
    private async Task CloseDocumentAsync(DocumentState doc)
    {
        if (doc.Dirty && !Dialogs.Confirm(this, "关闭并丢弃未保存的编辑？\n" + doc.File.Path)) return;
        _documents.Remove(doc);
        if (_active == doc)
        {
            _active = null;
            if (_documents.Count > 0) await OpenFileAsync(Relative(_documents[^1].File.Path), remember: false);
            else { _loadingDocument = true; Editor.Document = new TextDocument(); Editor.IsReadOnly = true; _loadingDocument = false; SetSurface(_showDiff && _preview != null); UpdateStructure(); }
        }
        RebuildTabs();
    }
    private async Task RefreshGitAsync()
    {
        var git = _git;
        if (git == null || _refreshingGit) return;
        _refreshingGit = true;
        try
        {
            var status = await git.StatusAsync();
            if (_git != git) return;
            _gitStatus = status;
            _lastRefresh = DateTime.UtcNow;
            BranchLabel.Text = status.Branch.Split("...")[0];
            BranchButton.ToolTip = status.Branch;
            BindChanges(status);
        }
        finally { _refreshingGit = false; }
        if (_git != git) return;
        if (_showDiff && _preview?.Change != null)
        {
            var current = _gitStatus!.Changes.FirstOrDefault(c => c.Path == _preview.Path && !c.IsConflict);
            if (current == null) ClosePreview();
            else await ShowPreviewAsync(DiffPreview.Local(current, current.IsStaged && (!current.HasWorkingChanges || _preview.Staged)));
        }
    }



    private void MoveDiff(int direction)
    {
        if (_diffLines.Length == 0) return;
        _diffIndex = (_diffIndex + direction + _diffLines.Length) % _diffLines.Length;
        _diffScroll.RevealLine(_diffLines[_diffIndex]);
        UiText.Set(DiffSummary, "Text", $"第 {_diffIndex + 1} / {_diffLines.Length} 处差异");
    }

    private async Task GitOperationAsync(string title, Func<GitService, Task> action)
    {
        if (_git == null) { SetStatus("当前不是 Git 仓库。可在 Git 菜单中初始化。"); return; }
        if (_commandBusy) { SetStatus("另一个命令正在运行。"); return; }
        _commandBusy = true;
        try { SetStatus(() => UiText.Current.Translate(title) + "…"); await action(_git); await RefreshGitAsync(); SetStatus(() => UiText.Current.Format($"{UiText.Current.Translate(title)}完成")); }
        finally { _commandBusy = false; }
    }
    private string[] SelectedPaths() => ChangesList.SelectedItems.Cast<ChangeItem>().Select(item => item.Change).Distinct().SelectMany(c => c.OriginalPath == null ? new[] { c.Path } : new[] { c.Path, c.OriginalPath }).Distinct().ToArray();

    private async Task SearchAsync(string mode = "all", string initial = "")
    {
        var commands = new Dictionary<string, (string Key, Func<Task> Action)>
        {
            [UiText.Current.Translate("打开项目")] = ("Ctrl+O", ChooseProjectAsync),
            [UiText.Current.Translate("提交")] = ("Ctrl+K", () => ActivateCommitAsync(false)),
            [UiText.Current.Translate("Git 日志")] = ("Alt+9", () => SelectBottomAsync(BottomTool.Log, false)),
            [UiText.Current.Translate("分支")] = ("Git", ShowBranchesAsync),
            [UiText.Current.Translate("推送")] = ("Ctrl+Shift+K", PushWithPreviewAsync),
            [UiText.Current.Translate("服务")] = ("Alt+8", () => SelectBottomAsync(BottomTool.Services, false)),
            [UiText.Current.Translate("刷新项目")] = ("F5", RefreshWorkspaceAsync),
            [UiText.Current.Translate("隐藏或恢复工具窗口")] = ("Ctrl+Shift+F12", () => { ToggleFocusMode(); return Task.CompletedTask; })
        };
        var actions = commands.Select(c => new SearchHit(c.Key, "", 0, "操作", c.Value.Key)).ToArray();
        var dialog = new SearchWindow(this, _workspace ?? new WorkspaceSnapshot("", [], [], [], [], false), mode, initial, actions: actions);
        if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;
        if (result.Kind == "操作" && commands.TryGetValue(result.Name, out var command)) await command.Action();
        else await OpenFileAsync(result.Path, result.Line);
    }

    private async Task NavigateDefinitionAsync(int? offset = null)
    {
        if (_workspace == null || _active == null) return;
        var position = Math.Clamp(offset ?? Editor.CaretOffset, 0, Editor.Document.TextLength);
        var text = Editor.Text;
        int start = position, end = position;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '$')) start--;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '$')) end++;
        if (start == end) return;
        var name = text[start..end];
        var hits = WorkspaceService.FindDefinitions(_workspace, name, Relative(_active.File.Path), text);
        if (hits.Count == 1) await OpenFileAsync(hits[0].Path, hits[0].Line);
        else if (hits.Count > 1)
        {
            var search = new SearchWindow(this, _workspace, "symbols", name, hits);
            if (search.ShowDialog() == true && search.Result is { } hit) await OpenFileAsync(hit.Path, hit.Line);
        }
        else SetStatus($"没有找到 {name} 的项目源码定义。此轻量索引不解析依赖 JAR、类型推断或动态代理。可按 F5 更新索引。");
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftShift or Key.RightShift) { if (!e.IsRepeat) _shiftOnly = true; return; }
        _shiftOnly = false; _lastShift = default;
        var modifiers = Keyboard.Modifiers;
        var ctrl = ModifierKeys.Control;
        var alt = ModifierKeys.Alt;
        var shift = ModifierKeys.Shift;
        Func<Task>? action = (modifiers, key) switch
        {
            (ModifierKeys.Alt, Key.D1) => () => { SelectLeft(LeftTool.Project); return Task.CompletedTask; }
            ,
            (ModifierKeys.Alt, Key.D0) => () => ActivateCommitAsync(true),
            (ModifierKeys.Alt, Key.D9) => () => SelectBottomAsync(BottomTool.Log),
            (ModifierKeys.Alt, Key.D8) => () => SelectBottomAsync(BottomTool.Services),
            (ModifierKeys.Alt, Key.D7) => () => { SelectLeft(LeftTool.Structure); return Task.CompletedTask; }
            ,
            (ModifierKeys.Alt, Key.D4) => () => SelectBottomAsync(BottomTool.Run),
            (ModifierKeys.Alt, Key.F12) => () => { OpenTerminal(); return Task.CompletedTask; }
            ,
            (ModifierKeys.Alt, Key.Home) => () => { SelectCurrentFile(); return Task.CompletedTask; }
            ,
            (ModifierKeys.Control, Key.K) => () => ActivateCommitAsync(false),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.K) => PushWithPreviewAsync,
            (ModifierKeys.Control | ModifierKeys.Shift, Key.Oem3) => ShowBranchesAsync,
            (ModifierKeys.Control, Key.D) when ChangesList.IsKeyboardFocusWithin => OpenSelectedChangeAsync,
            (ModifierKeys.Control, Key.D) when HistoryFiles.IsKeyboardFocusWithin => OpenHistoryFileAsync,
            (ModifierKeys.None, Key.F4) when ChangesList.IsKeyboardFocusWithin => OpenSelectedSourceAsync,
            (ModifierKeys.None, Key.Enter) when ChangesList.IsKeyboardFocusWithin => OpenSelectedChangeAsync,
            (ModifierKeys.None, Key.Enter) when HistoryFiles.IsKeyboardFocusWithin => OpenHistoryFileAsync,
            (ModifierKeys.None, Key.F4) when _showDiff && _preview != null => () => OpenFileAsync(_preview.Path),
            (ModifierKeys.Control | ModifierKeys.Alt, Key.A) when ChangesList.IsKeyboardFocusWithin => StageSelectionAsync,
            (ModifierKeys.Control | ModifierKeys.Alt, Key.Z) when ChangesList.IsKeyboardFocusWithin => DiscardSelectionAsync,
            (ModifierKeys.Control, Key.Enter) when CommitPane.IsKeyboardFocusWithin => CommitAsync,
            (ModifierKeys.Control, Key.O) => ChooseProjectAsync,
            (ModifierKeys.Control, Key.T) => () => RunGitCommandAsync(["pull", "--ff-only"]),
            (ModifierKeys.Control, Key.F9) => () => RunConfiguredMavenAsync("compile"),
            (ModifierKeys.Shift, Key.F10) => () => RunConfiguredMavenAsync(),
            (ModifierKeys.Control | ModifierKeys.Alt, Key.N) => CreateBranchAsync,
            (ModifierKeys.Control, Key.S) => () => SaveAsync(),
            (ModifierKeys.Control, Key.P) => () => SearchAsync(),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.N) => () => SearchAsync("files"),
            (ModifierKeys.Control, Key.N) => () => SearchAsync("classes"),
            (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt, Key.N) => () => SearchAsync("symbols"),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.F) => () => SearchAsync("text", Editor.SelectedText.Length < 100 ? Editor.SelectedText : ""),
            (ModifierKeys.Control, Key.B) or (ModifierKeys.None, Key.F12) when !_showDiff => () => NavigateDefinitionAsync(),
            (ModifierKeys.None, Key.F5) => RefreshWorkspaceAsync,
            (ModifierKeys.Control, Key.W) when _showDiff => () => { ClosePreview(); return Task.CompletedTask; }
            ,
            (ModifierKeys.Control, Key.W) when _active != null => () => CloseDocumentAsync(_active),
            (ModifierKeys.Alt, Key.Left) or (ModifierKeys.Control | ModifierKeys.Alt, Key.Left) => GoBackAsync,
            _ => null
        };
        if (action != null) { e.Handled = true; await Guard(action); return; }
        if (modifiers == (ctrl | shift) && key == Key.F12) ToggleFocusMode();
        else if (modifiers == (shift | ctrl) && key == Key.A) { await Guard(() => SearchAsync("actions")); }
        else if (modifiers == (ctrl | alt) && key == Key.S) ShowSettingsMenu();
        else if (modifiers == shift && key == Key.Escape) HideFocusedTool();
        else if (_showDiff && key == Key.F7 && (modifiers == shift || modifiers == ModifierKeys.None)) MoveDiff(modifiers == shift ? -1 : 1);
        else if (!_showDiff && Editor.IsKeyboardFocusWithin && _active != null && modifiers == ctrl && key == Key.H) ReplaceText();
        else if (!_showDiff && _active != null && modifiers == ctrl && key == Key.G) GoToLine();
        else if (!_showDiff && _active != null && modifiers == (ctrl | alt) && key == Key.L) NormalizeIndentation();
        else if (modifiers == ctrl && key == Key.D && Editor.IsKeyboardFocusWithin && _active != null) DuplicateLine();
        else if (modifiers == ctrl && key == Key.OemQuestion && Editor.IsKeyboardFocusWithin && _active != null) ToggleComment();
        else if (key == Key.Tab && (modifiers == ctrl || modifiers == (ctrl | shift))) await Guard(() => CycleTabAsync(modifiers.HasFlag(shift)));
        else if (key == Key.Escape && modifiers == ModifierKeys.None && !Editor.IsKeyboardFocusWithin && !CommitMessage.IsKeyboardFocusWithin) FocusEditor();
        else return;
        e.Handled = true;
    }

    private async void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.LeftShift or Key.RightShift) || !_shiftOnly || Keyboard.Modifiers != ModifierKeys.None) return;
        _shiftOnly = false;
        if ((DateTime.UtcNow - _lastShift).TotalMilliseconds < 400) { _lastShift = default; e.Handled = true; await Guard(() => SearchAsync()); }
        else _lastShift = DateTime.UtcNow;
    }
    private void GoToLine()
    {
        var value = Dialogs.Prompt(this, "跳转到行", "输入行号（可使用 行:列）", Editor.TextArea.Caret.Line.ToString());
        var parts = value?.Split(':');
        if (parts == null || !int.TryParse(parts[0], out var line)) return;
        line = Math.Clamp(line, 1, Editor.Document.LineCount);
        var column = parts.Length > 1 && int.TryParse(parts[1], out var c) ? Math.Max(c, 1) : 1;
        Editor.TextArea.Caret.Position = new ICSharpCode.AvalonEdit.TextViewPosition(line, Math.Min(column, Editor.Document.GetLineByNumber(line).Length + 1));
        Editor.ScrollToLine(line); Editor.TextArea.Focus();
    }
    private void ToggleFocusMode()
    {
        RememberSizes();
        _focusMode = !_focusMode;
        ApplyLayout();
        if (_focusMode) FocusEditor();
    }

    private void NormalizeIndentation()
    {
        if (_active == null) return;
        var offset = Editor.CaretOffset;
        Editor.Document.Replace(0, Editor.Document.TextLength, _active.File.Style.NormalizeIndentation(Editor.Text));
        Editor.CaretOffset = Math.Min(offset, Editor.Document.TextLength);
        SetStatus("已按文件风格统一缩进空白。此操作不重排 Java 语句，可 Ctrl+Z 撤销。");
    }
    private void ReplaceText()
    {
        var find = Dialogs.Prompt(this, "替换文本", "查找内容（区分大小写，按字面文本匹配）", Editor.SelectedText.Length < 200 ? Editor.SelectedText : "");
        if (string.IsNullOrEmpty(find)) return;
        var replacement = Dialogs.Prompt(this, "替换文本", "替换为（留空表示删除）");
        if (replacement == null) return;
        var count = Regex.Matches(Editor.Text, Regex.Escape(find)).Count;
        if (count == 0) { SetStatus("没有匹配的文本。"); return; }
        if (!Dialogs.Confirm(this, $"替换当前文件中的 {count} 处匹配？可使用 Ctrl+Z 撤销。")) return;
        Editor.Document.Replace(0, Editor.Document.TextLength, Editor.Text.Replace(find, replacement, StringComparison.Ordinal));
    }
    private void DuplicateLine()
    {
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        var value = Editor.Document.GetText(line.Offset, line.TotalLength);
        if (line.DelimiterLength == 0) value = (_active?.File.Style.NewLine ?? "\n") + value;
        Editor.Document.Insert(line.Offset + line.TotalLength, value);
    }
    private void ToggleComment()
    {
        var ext = Path.GetExtension(_active!.File.Path);
        var prefix = ext is ".yml" or ".yaml" or ".properties" or ".py" or ".sh" or ".ps1" ? "#" : "//";
        if (ext is ".xml" or ".xaml" or ".html" or ".md" or ".json") { SetStatus("此文件类型暂不提供行注释快捷键。"); return; }
        var first = Editor.Document.GetLineByOffset(Editor.SelectionStart).LineNumber;
        var last = Editor.Document.GetLineByOffset(Editor.SelectionStart + Math.Max(0, Editor.SelectionLength - 1)).LineNumber;
        var lines = Enumerable.Range(first, last - first + 1).Select(Editor.Document.GetLineByNumber).ToArray();
        var remove = lines.All(l => Editor.Document.GetText(l).TrimStart().StartsWith(prefix));
        using (Editor.Document.RunUpdate())
            foreach (var line in lines.Reverse())
            {
                var value = Editor.Document.GetText(line); var spaces = value.Length - value.TrimStart().Length;
                if (remove) Editor.Document.Remove(line.Offset + spaces, prefix.Length + (value.Length > spaces + prefix.Length && value[spaces + prefix.Length] == ' ' ? 1 : 0));
                else Editor.Document.Insert(line.Offset + spaces, prefix + " ");
            }
    }

    private async Task ChooseProjectAsync()
    {
        var picker = new OpenFolderDialog { Title = UiText.Current.Translate("选择本地项目 / Git 仓库"), Multiselect = false };
        if (_workspace != null && Directory.Exists(_workspace.Root)) picker.InitialDirectory = Path.GetFullPath(_workspace.Root);
        if (picker.ShowDialog(this) == true) await OpenWorkspaceAsync(picker.FolderName);
    }
    private async Task RefreshWorkspaceAsync()
    {
        if (_workspace == null) return;
        _workspaceLoad?.Cancel(); _workspaceLoad?.Dispose(); _workspaceLoad = new();
        var token = _workspaceLoad.Token; var root = _workspace.Root;
        SetStatus("刷新文件与源码索引…");
        var snapshot = await WorkspaceService.LoadAsync(root, _git, token);
        token.ThrowIfCancellationRequested();
        _workspace = snapshot;
        RenderProjectTree();
        SpringList.ItemsSource = snapshot.Spring;
        PopulateMavenTree();
        UpdateStructure();

        // Reload clean buffers changed by Git/AI tools, keeping dirty buffers intact.
        foreach (var doc in _documents.Where(d => !d.Dirty).ToArray())
        {
            if (!File.Exists(doc.File.Path)) continue;
            var fresh = await FileService.ReadAsync(doc.File.Path, token);
            if (fresh.Hash == doc.File.Hash || doc.Dirty) continue;
            _loadingDocument = true; doc.File = fresh; doc.Document.Text = fresh.Text; _loadingDocument = false;
        }
        await RefreshGitAsync();
        if (_bottom == BottomTool.Log) await LoadHistoryAsync();
        SetStatus("索引与 Git 状态已刷新");
    }
    private async Task GoBackAsync()
    {
        if (_back.TryPop(out var target)) await OpenFileAsync(target.Path, target.Line, false);
    }
    private async void OnActivated(object? sender, EventArgs e)
    {
        if (IsLoaded && _workspace != null && !_commandBusy && (DateTime.UtcNow - _lastRefresh).TotalSeconds > 3)
            await Guard(async () =>
            {
                await RefreshGitAsync();
                var doc = _active;
                if (doc == null || !File.Exists(doc.File.Path)) return;
                var fresh = await FileService.ReadAsync(doc.File.Path);
                if (fresh.Hash == doc.File.Hash) return;
                if (doc.Dirty) { SetStatus("当前文件被外部程序修改；编辑内容已保留，保存前会再次确认。"); return; }
                var offset = Editor.CaretOffset;
                _loadingDocument = true; doc.File = fresh; doc.Document.Text = fresh.Text;
                if (_active == doc) Editor.CaretOffset = Math.Min(offset, doc.Document.TextLength);
                _loadingDocument = false;
                SetStatus("已同步 AI / 外部工具修改的文件 · F5 可更新代码索引");
            });
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmCloseAll()) { e.Cancel = true; return; }
        if (_commandBusy && !Dialogs.Confirm(this, "仍有 Git / Maven 命令运行中，关闭会停止该命令。是否继续？")) { e.Cancel = true; return; }
        RememberSizes(); _layout.Save();
        _command?.Cancel(); _workspaceLoad?.Cancel(); _diffLoad?.Cancel(); _historyLoad?.Cancel(); _detailLoad?.Cancel(); _memoryTimer.Stop(); _outputTimer.Stop();
    }

    private async void OpenProjectClick(object sender, RoutedEventArgs e) => await Guard(ChooseProjectAsync);
    private async void SearchClick(object sender, RoutedEventArgs e) => await Guard(() => SearchAsync());
    private async void SaveClick(object sender, RoutedEventArgs e) => await Guard(() => SaveAsync());
    private async void BackClick(object sender, RoutedEventArgs e) => await Guard(GoBackAsync);
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Guard(RefreshWorkspaceAsync);
    private async void GitRefreshClick(object sender, RoutedEventArgs e) => await Guard(RefreshGitAsync);
    private async void TreeDoubleClick(object sender, MouseButtonEventArgs e) { if (FileTree.SelectedItem is TreeViewItem { Tag: FileNode { IsFile: true } node }) { e.Handled = true; await Guard(() => OpenFileAsync(node.Path)); } }
    private async void TreeOpenClick(object sender, RoutedEventArgs e) { if (FileTree.SelectedItem is TreeViewItem { Tag: FileNode { IsFile: true } node }) await Guard(() => OpenFileAsync(node.Path)); }
    private async void TreeKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && FileTree.SelectedItem is TreeViewItem { Tag: FileNode { IsFile: true } node }) { e.Handled = true; await Guard(() => OpenFileAsync(node.Path)); } }
    private void CopyPathClick(object sender, RoutedEventArgs e) { if (FileTree.SelectedItem is TreeViewItem { Tag: FileNode node }) Clipboard.SetText(node.Path); }
    private void RevealClick(object sender, RoutedEventArgs e)
    {
        if (_workspace == null) return;
        var path = FileTree.SelectedItem is TreeViewItem { Tag: FileNode node } ? FileService.SafePath(_workspace.Root, node.Path) : _workspace.Root;
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; info.ArgumentList.Add("/select,"); info.ArgumentList.Add(path); Process.Start(info);
    }
    private async void NewFileClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_workspace == null) return;
        var name = Dialogs.Prompt(this, "新建文件", "相对于项目根目录的路径，例如 src/main/java/demo/Hello.java");
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = FileService.SafePath(_workspace.Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
        await RefreshWorkspaceAsync(); await OpenFileAsync(Relative(path));
    });
    private async void EditorMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (position != null) { e.Handled = true; await Guard(() => NavigateDefinitionAsync(Editor.Document.GetOffset(position.Value.Location))); }
    }
    private async void ChangeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt<ListBoxItem>(ChangesList, e.OriginalSource) != null) await Guard(OpenSelectedChangeAsync);
    }
    private async void PreviewChangeClick(object sender, RoutedEventArgs e) => await Guard(OpenSelectedChangeAsync);
    private async void EditChangeClick(object sender, RoutedEventArgs e) => await Guard(OpenSelectedSourceAsync);
    private async void EditDiffSourceClick(object sender, RoutedEventArgs e) { if (_preview != null) await Guard(() => OpenFileAsync(_preview.Path)); }
    private async void DiffModeClick(object sender, RoutedEventArgs e) { if (_preview != null) await Guard(() => ShowPreviewAsync(_preview)); }
    private void PreviousDiffClick(object sender, RoutedEventArgs e) => MoveDiff(-1);
    private void NextDiffClick(object sender, RoutedEventArgs e) => MoveDiff(1);
    private async Task StageSelectionAsync()
    {
        if (ChangesList.SelectedItems.Cast<ChangeItem>().Any(item => item.Change.IsConflict))
            throw new InvalidOperationException("请先双击冲突文件，在合并窗口中应用结果。");
        await GitOperationAsync("暂存", async git => { var paths = SelectedPaths(); if (paths.Length > 0) await git.StageAsync(paths); });
    }
    private async void StageClick(object sender, RoutedEventArgs e) => await Guard(StageSelectionAsync);
    private async void UnstageClick(object sender, RoutedEventArgs e) => await Guard(() => GitOperationAsync("取消暂存", async git => { var paths = SelectedPaths(); if (paths.Length > 0) await git.UnstageAsync(paths); }));
    private async void StageAllClick(object sender, RoutedEventArgs e) => await Guard(() => GitOperationAsync("全部暂存", async git =>
    {
        if (_gitStatus?.Changes.Any(c => c.IsConflict) == true) throw new InvalidOperationException("存在冲突，请先解决冲突。");
        if (_gitStatus?.Changes.Count > 0) await git.StageAsync(["."]);
    }));
    private async Task<bool> CommitStagedAsync()
    {
        if (_git == null || _commandBusy) return false;
        bool amend = AmendCheck.IsChecked == true;
        if (amend && !Dialogs.Confirm(this, "用勾选文件的内容修改上一次提交？这会改写该提交的 ID。")) return false;
        await GitOperationAsync("提交", async git =>
        {
            var paths = _commitChoices.Where(pair => pair.Value.Included).Select(pair => pair.Key).ToArray();
            foreach (var doc in _documents.Where(d => d.Dirty && paths.Contains(Relative(d.File.Path))).ToArray())
            {
                await OpenFileAsync(Relative(doc.File.Path));
                await SaveAsync();
                if (doc.Dirty) throw new InvalidOperationException("勾选文件仍有未保存的编辑，请保存后重试。");
            }
            await git.CommitSelectedAsync(paths, CommitMessage.Text, amend);
            CommitMessage.Clear(); AmendCheck.IsChecked = false;
        });
        if (_bottom == BottomTool.Log) await LoadHistoryAsync();
        return true;
    }
    private async Task CommitAsync() => await CommitStagedAsync();
    private async void CommitClick(object sender, RoutedEventArgs e) => await Guard(CommitAsync);
    private async void CommitPushClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (await CommitStagedAsync()) await PushWithPreviewAsync();
    });
    private async Task DiscardSelectionAsync() => await GitOperationAsync("回滚改动", async git =>
    {
        var changes = ChangesList.SelectedItems.Cast<ChangeItem>().Select(c => c.Change).Distinct().ToArray();
        if (changes.Length == 0 || !Dialogs.Confirm(this, "丢弃以下工作区修改？未跟踪文件将被删除。\n\n" + string.Join('\n', changes.Select(c => c.Path)))) return;
        foreach (var change in changes)
        {
            if (change.IsUntracked) File.Delete(FileService.SafePath(git.Root, change.Path));
            else (await git.RunAsync(["restore", "--worktree", "--", change.Path])).EnsureSuccess();
        }
        await RefreshWorkspaceAsync();
    });
    private async void DiscardClick(object sender, RoutedEventArgs e) => await Guard(DiscardSelectionAsync);
    private async void HistoryClick(object sender, RoutedEventArgs e) => await Guard(LoadHistoryAsync);
    private async void SpringDoubleClick(object sender, MouseButtonEventArgs e) { if (SpringList.SelectedItem is SearchHit hit) await Guard(() => OpenFileAsync(hit.Path, hit.Line)); }

    private async void MavenDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TreeItemAt(e.OriginalSource) is not { } item) return;
        e.Handled = true;
        if (item.Tag is MavenTask task) await Guard(() => RunMavenAsync(task.Module, task.Goal));
        else if (item.Tag is MavenModule module) await Guard(() => OpenFileAsync(module.Path));
    }
    private void MavenSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var tag = (MavenList.SelectedItem as TreeViewItem)?.Tag;
        var module = tag is MavenTask task ? task.Module : tag as MavenModule;
        UiText.Set(MavenDetails, "Text", () => module != null
            ? $"{module.Group}:{module.Artifact}:{module.Version}\n{module.Path}\n\n" + string.Join('\n', module.Dependencies.Select(DependencyLabel)) + UiText.Current.Translate(module.Error) : "");
    }
    private void ShowOutput(string title, string output, bool localizeMessage = false)
    {
        OutputTitle.Text = title;
        _outputMessage = localizeMessage ? output : null;
        OutputEditor.Text = localizeMessage ? UiText.Current.Translate(output) : output;
        if (title.StartsWith("git ", StringComparison.Ordinal)) BuildTree.Items.Clear();
        CancelBuildButton.IsEnabled = _commandBusy;
        UiText.Set(BuildResult, "Text", title.StartsWith("git ", StringComparison.Ordinal) ? "Git" : "构建");
        _bottom = title.StartsWith("git ", StringComparison.Ordinal) ? BottomTool.Console : BottomTool.Run;
        _focusMode = false;
        ApplyLayout();
    }

    private void CancelCommandClick(object sender, RoutedEventArgs e) => _command?.Cancel();
    private void HelpClick(object sender, RoutedEventArgs e) => Dialogs.Information(this, "Idea Lite · 快捷键",
        "Alt+1  项目      Alt+0 / Ctrl+K  提交      Alt+9  Git 日志\nAlt+7  结构      Alt+8  服务      Alt+4  运行输出\n双击 Shift  随处搜索      Ctrl+Shift+A  查找操作\nCtrl+N  类      Ctrl+Shift+N  文件      Ctrl+Alt+Shift+N  符号\nCtrl+Shift+F  项目文本      Ctrl+F  当前文件查找\nCtrl+D  在改动列表查看 Diff，在编辑器复制行\nF4  打开工作区文件      F7 / Shift+F7  下一个 / 上一个差异\nCtrl+B / Ctrl+单击  定义      Ctrl+Alt+Left  返回\nCtrl+Alt+A  暂存所选      Ctrl+Enter  提交勾选文件\nCtrl+Shift+K  推送      Ctrl+Shift+F12  隐藏或恢复工具窗口\nCtrl+S  保存      Ctrl+Alt+L  匹配缩进      Ctrl+W  关闭标签");
}

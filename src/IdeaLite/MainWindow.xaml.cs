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
    private bool _commandBusy, _loadingDocument, _refreshingGit, _switchingView;
    private readonly List<DocumentState> _documents = [];
    private readonly Stack<(string Path, int Line)> _back = new();
    private DocumentState? _active;
    private DateTime _lastShift, _lastRefresh;
    private bool _shiftOnly;
    private readonly DispatcherTimer _memoryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private int[] _diffLines = [];
    private int _diffIndex = -1;
    private bool _syncScrolling;
    private bool _focusMode;
    private int _openRequest;
    private double _explorerWidth = 238, _changesWidth = 295;
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
        foreach (var editor in new[] { OldEditor, NewEditor, HistoryEditor, CommitEditor, OutputEditor }) EditorSupport.Configure(editor);
        HistoryEditor.ShowLineNumbers = false; HistoryEditor.FontSize = 13; OutputEditor.FontSize = 12;
        CommitEditor.TextArea.TextView.LineTransformers.Add(new PatchColorizer());
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaret();
        Editor.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Editor.FontSize = Math.Clamp(Editor.FontSize + (e.Delta > 0 ? 1 : -1), 10, 26); e.Handled = true; }
        };
        OldEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SyncScroll(OldEditor, NewEditor);
        NewEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SyncScroll(NewEditor, OldEditor);
        Editor.Text = "\n\n    轻装看代码。\n\n    打开项目，直接进入工作。\n    审查 AI 改动，不必等整个 IDE 启动。\n\n    Ctrl + O           打开本地项目\n    Shift × 2          搜索文件、类型和方法\n    Ctrl + Shift + F   搜索项目内容\n    Ctrl + 鼠标左键    查找项目内定义\n    Ctrl + F           查找当前文件\n    Ctrl + K           编写提交说明\n\n    原生界面 · 无常驻 JVM · 本地 Git\n\n    Maven / Spring 识别不会运行项目脚本或下载依赖。";
        Editor.IsReadOnly = true;
        _memoryTimer.Tick += (_, _) => UpdateMemory(); _memoryTimer.Start(); UpdateMemory();
        Loaded += async (_, _) => { if (_initialRoot != null) await Guard(() => OpenWorkspaceAsync(_initialRoot)); };
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(ex.Message); MessageBox.Show(this, ex.Message, "Idea Lite", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void SetStatus(string text) => StatusLabel.Text = text.Replace('\n', ' ').Replace('\r', ' ');
    private void UpdateMemory()
    {
        using var process = Process.GetCurrentProcess();
        MemoryLabel.Text = $"RAM {process.WorkingSet64 / 1048576.0:F0} MB";
        MemoryLabel.ToolTip = $"当前进程工作集；专用内存 {process.PrivateMemorySize64 / 1048576.0:F0} MB。Git/Maven 子进程另计。";
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
        _diffLoad?.Cancel();
        var token = _workspaceLoad.Token;
        _documents.Clear(); _active = null; _back.Clear(); _workspace = null; _git = null; _gitStatus = null;
        _loadingDocument = true; Editor.Document = new TextDocument(); Editor.IsReadOnly = true; _loadingDocument = false;
        RebuildTabs(); FileTree.Items.Clear(); ChangesList.ItemsSource = null; MavenList.ItemsSource = null; SpringList.ItemsSource = null;
        OldEditor.Clear(); NewEditor.Clear(); HistoryEditor.Clear(); CommitEditor.Clear(); CommitMessage.Clear(); Views.SelectedIndex = 0;
        root = Path.GetFullPath(root);
        SetStatus("正在打开项目与建立有上限的源码索引…");
        string? gitRoot;
        try { gitRoot = await GitService.FindRootAsync(root, token); }
        catch (Win32Exception) { gitRoot = null; SetStatus("未找到 Git，可浏览代码；请安装 Git 并加入 PATH。"); }
        root = gitRoot ?? root;
        var git = gitRoot == null ? null : new GitService(root);
        var snapshot = await WorkspaceService.LoadAsync(root, git, token);
        token.ThrowIfCancellationRequested();
        _workspace = snapshot; _git = git;
        ProjectTitle.Text = Path.GetFileName(root); ProjectTitle.ToolTip = root;
        Title = $"{Path.GetFileName(root)} — Idea Lite";
        FileCount.Text = snapshot.Files.Length.ToString("N0");
        IndexLabel.Text = $"{snapshot.Symbols.Count:N0} 个代码符号 · {snapshot.Maven.Count} 个 Maven 模块" + (snapshot.Limited ? "\n索引已达到限制，可缩小项目范围" : "\n按需索引 · 无常驻 JVM");
        PopulateTree(FileTree, "");
        MavenList.ItemsSource = snapshot.Maven; SpringList.ItemsSource = snapshot.Spring;
        BranchButton.Content = git == null ? "⑂  非 Git 项目" : "⑂  加载分支…";
        if (git != null) await RefreshGitAsync();
        var first = snapshot.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("README.md", StringComparison.OrdinalIgnoreCase)) ?? snapshot.Files.FirstOrDefault(p => p.EndsWith("pom.xml"));
        if (first != null) await OpenFileAsync(first);
        SetStatus($"已打开 {root} · {snapshot.Files.Length:N0} 个文件 · {snapshot.Symbols.Count:N0} 个符号" + (snapshot.Limited ? " · 索引达到上限" : ""));
    }

    private void PopulateTree(ItemsControl parent, string prefix)
    {
        if (_workspace == null) return;
        parent.Items.Clear();
        var groups = _workspace.Files.Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
            .GroupBy(p => p[prefix.Length..].Split('/')[0]).OrderBy(g => g.Key.Contains('.') ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var path = prefix + group.Key;
            var isFile = group.Any(p => p == path);
            var name = group.Key;
            if (!isFile)
            {
                while (true)
                {
                    var tails = group.Select(p => p[(path.Length + 1)..]).ToArray();
                    if (tails.Any(t => !t.Contains('/'))) break;
                    var heads = tails.Select(t => t.Split('/')[0]).Distinct().ToArray();
                    if (heads.Length != 1) break;
                    path += "/" + heads[0]; name += "/" + heads[0];
                }
            }
            var header = new TextBlock { Text = (isFile ? FileIcon(path) : "▸ ") + name, FontSize = 12, ToolTip = path };
            if (path.EndsWith(".java") || path.EndsWith("pom.xml")) header.Foreground = new SolidColorBrush(Color.FromRgb(181, 207, 168));
            var item = new TreeViewItem { Header = header, Tag = new FileNode(path, isFile) };
            if (!isFile)
            {
                item.Items.Add(new TreeViewItem { Header = "…" });
                item.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, item) && item.Items.Count == 1 && ((TreeViewItem)item.Items[0]).Tag == null) PopulateTree(item, path + "/"); };
            }
            parent.Items.Add(item);
        }
    }
    private static string FileIcon(string path) => Path.GetExtension(path) switch { ".java" => "J  ", ".kt" => "K  ", ".xml" => "◇  ", ".md" => "≡  ", ".yml" or ".yaml" or ".properties" => "⚙  ", _ => "·  " };

    private async Task OpenFileAsync(string relative, int line = 1, bool remember = true)
    {
        if (_workspace == null) return;
        var request = ++_openRequest;
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
            captured.Document.Changed += (_, _) => { if (!_loadingDocument) { captured.Dirty = true; RebuildTabs(); } };
        }
        if (_active != null)
        {
            _active.Caret = Editor.CaretOffset; _active.Scroll = Editor.VerticalOffset;
            if (remember && (_active != document || Editor.TextArea.Caret.Line != line)) _back.Push((Relative(_active.File.Path), Editor.TextArea.Caret.Line));
        }
        _loadingDocument = true; _active = document;
        Editor.Document = document.Document; Editor.IsReadOnly = false;
        Editor.Options.ConvertTabsToSpaces = !document.File.Style.UseTabs;
        Editor.Options.IndentationSize = document.File.Style.IndentSize;
        EditorSupport.Highlight(Editor, path);
        Editor.CaretOffset = Math.Min(document.Caret, document.Document.TextLength);
        Editor.ScrollToVerticalOffset(document.Scroll);
        _loadingDocument = false;
        Views.SelectedIndex = 0; Breadcrumb.Text = relative.Replace('/', '›');
        EditorConflictBar.Visibility = _gitStatus?.Changes.Any(c => c.Path == relative && c.IsConflict) == true ? Visibility.Visible : Visibility.Collapsed;
        if (line > 1) { line = Math.Clamp(line, 1, document.Document.LineCount); Editor.TextArea.Caret.Line = line; Editor.ScrollToLine(line); }
        RebuildTabs(); UpdateCaret(); Editor.TextArea.Focus();
        SetStatus(document.File.Style.Source + " · 保存时保留/匹配文件风格 · Ctrl+Alt+L 统一缩进空白");
    }
    private string Relative(string path) => _workspace == null ? path : Path.GetRelativePath(_workspace.Root, path).Replace('\\', '/');
    private void RebuildTabs()
    {
        if (DocumentTabs == null) return;
        DocumentTabs.Children.Clear();
        foreach (var doc in _documents)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var button = new Button
            {
                Content = (doc.Dirty ? "● " : "") + Path.GetFileName(doc.File.Path),
                Padding = new Thickness(9, 3, 9, 3),
                ToolTip = doc.File.Path,
                Foreground = new SolidColorBrush(doc == _active ? Color.FromRgb(125, 226, 178) : Color.FromRgb(160, 172, 190))
            };
            button.Click += async (_, _) => await Guard(() => OpenFileAsync(Relative(doc.File.Path)));
            var close = new Button { Content = "×", Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(0, 3, 4, 3), ToolTip = "关闭 · Ctrl+W" };
            close.Click += async (_, _) => await Guard(() => CloseDocumentAsync(doc));
            row.Children.Add(button); row.Children.Add(close); DocumentTabs.Children.Add(row);
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
            else { _loadingDocument = true; Editor.Document = new TextDocument(); Editor.IsReadOnly = true; _loadingDocument = false; Breadcrumb.Text = "WORKSPACE"; }
        }
        RebuildTabs();
    }
    private void EditorChanged(object? sender, EventArgs e) { }

    private async Task RefreshGitAsync()
    {
        var git = _git;
        if (git == null || _refreshingGit) return;
        _refreshingGit = true;
        try
        {
            var status = await git.StatusAsync();
            if (_git != git) return;
            _gitStatus = status; _lastRefresh = DateTime.UtcNow;
            var selected = (ChangesList.SelectedItem as GitChange)?.Path;
            ChangesList.ItemsSource = status.Changes; ChangeCount.Text = status.Changes.Count.ToString();
            BranchButton.Content = "⑂  " + status.Branch; BranchButton.ToolTip = status.Branch;
            GitSummary.Text = status.Changes.Count == 0 ? "✓ 工作区干净。继续专注写代码。" : $"{status.Changes.Count(c => c.IsStaged)} 个文件已暂存 · {status.Changes.Count(c => c.IsConflict)} 个冲突 · 点击查看对比";
            if (selected != null) ChangesList.SelectedItem = status.Changes.FirstOrDefault(c => c.Path == selected);
        }
        finally { _refreshingGit = false; }
        if (_git == git && Views.SelectedIndex == 1)
        {
            if (ChangesList.SelectedItem is GitChange current) await ShowDiffAsync(current);
            else { OldEditor.Clear(); NewEditor.Clear(); DiffTitle.Text = "选择一个改动查看对比"; ConflictBar.Visibility = Visibility.Collapsed; }
        }
    }

    private async Task ShowDiffAsync(GitChange change)
    {
        var git = _git; if (git == null) return;
        _diffLoad?.Cancel(); _diffLoad?.Dispose(); _diffLoad = new();
        var token = _diffLoad.Token;
        var staged = StagedToggle.IsChecked == true;
        if (!change.HasWorkingChanges && change.IsStaged && !staged) { StagedToggle.IsChecked = true; staged = true; }
        if (!change.IsStaged || change.IsConflict) { StagedToggle.IsChecked = false; staged = false; }
        SetStatus("正在读取差异…");
        var diff = change.IsConflict ? await git.ConflictSidesAsync(change.Path, token) : await git.DiffAsync(change, staged, token);
        token.ThrowIfCancellationRequested();
        if (diff.OldText.Contains('\0') || diff.NewText.Contains('\0')) throw new IOException("二进制文件无法进行文本对比，请使用外部工具。");
        if (diff.OldText.Length + diff.NewText.Length > 1_000_000 || diff.OldText.Count(c => c == '\n') + diff.NewText.Count(c => c == '\n') > 40_000)
            throw new IOException("差异超过轻量视图限制（总计 100 万字符 / 4 万行）。请使用终端 git diff 或缩小文件。");
        var ignore = IgnoreWhitespace.IsChecked == true;
        var model = await Task.Run(() => new SideBySideDiffBuilder(new Differ()).BuildDiffModel(diff.OldText, diff.NewText, ignore), token);
        token.ThrowIfCancellationRequested();
        OldEditor.Text = string.Join('\n', model.OldText.Lines.Select(l => l.Text ?? ""));
        NewEditor.Text = string.Join('\n', model.NewText.Lines.Select(l => l.Text ?? ""));
        EditorSupport.Highlight(OldEditor, change.Path); EditorSupport.Highlight(NewEditor, change.Path);
        EditorSupport.SetLineColors(OldEditor, model.OldText.Lines, true); EditorSupport.SetLineColors(NewEditor, model.NewText.Lines, false);
        OldLabel.Text = diff.OldLabel; NewLabel.Text = diff.NewLabel; DiffTitle.Text = change.Path;
        var added = model.NewText.Lines.Count(l => l.Type is ChangeType.Inserted or ChangeType.Modified);
        var removed = model.OldText.Lines.Count(l => l.Type is ChangeType.Deleted or ChangeType.Modified);
        _diffLines = model.NewText.Lines.Select((l, i) => (l, i)).Where(x => x.l.Type != ChangeType.Unchanged && (x.i == 0 || model.NewText.Lines[x.i - 1].Type == ChangeType.Unchanged)).Select(x => x.i + 1).ToArray();
        _diffIndex = -1; ConflictBar.Visibility = change.IsConflict ? Visibility.Visible : Visibility.Collapsed;
        Views.SelectedIndex = 1; SetStatus($"{change.Path} · +{added} / −{removed}" + (change.IsConflict ? " · 上方显示两侧版本；在代码页编辑合并结果" : " · 空白行用于对齐，行号为对比视图行号"));
        MoveDiff(1);
    }
    private void SyncScroll(TextEditor source, TextEditor target)
    {
        if (_syncScrolling) return;
        _syncScrolling = true; target.ScrollToVerticalOffset(source.VerticalOffset); _syncScrolling = false;
    }
    private void MoveDiff(int direction)
    {
        if (_diffLines.Length == 0) return;
        _diffIndex = (_diffIndex + direction + _diffLines.Length) % _diffLines.Length;
        OldEditor.ScrollToLine(_diffLines[_diffIndex]); NewEditor.ScrollToLine(_diffLines[_diffIndex]);
    }

    private async Task GitOperationAsync(string title, Func<GitService, Task> action)
    {
        if (_git == null) { SetStatus("当前不是 Git 仓库。可在 Git 菜单中初始化。"); return; }
        if (_commandBusy) { SetStatus("另一个命令正在运行。"); return; }
        _commandBusy = true;
        try { SetStatus(title + "…"); await action(_git); await RefreshGitAsync(); SetStatus(title + "完成"); }
        finally { _commandBusy = false; }
    }
    private string[] SelectedPaths() => ChangesList.SelectedItems.Cast<GitChange>().SelectMany(c => c.OriginalPath == null ? new[] { c.Path } : new[] { c.Path, c.OriginalPath }).Distinct().ToArray();

    private async Task SearchAsync(string mode = "all", string initial = "")
    {
        if (_workspace == null) { SetStatus("请先打开项目。"); return; }
        var dialog = new SearchWindow(this, _workspace, mode, initial);
        if (dialog.ShowDialog() == true && dialog.Result is { } result) await OpenFileAsync(result.Path, result.Line);
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
        if (e.Key is Key.LeftShift or Key.RightShift) { if (!e.IsRepeat) _shiftOnly = true; return; }
        _shiftOnly = false; _lastShift = default;
        var modifiers = Keyboard.Modifiers;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control), shift = modifiers.HasFlag(ModifierKeys.Shift), alt = modifiers.HasFlag(ModifierKeys.Alt);
        Func<Task>? action = e.Key switch
        {
            Key.O when ctrl => ChooseProjectAsync,
            Key.S when ctrl => () => SaveAsync(),
            Key.P when ctrl => () => SearchAsync(),
            Key.N when ctrl && shift && !alt => () => SearchAsync(),
            Key.N when ctrl => () => SearchAsync("symbols"),
            Key.F when ctrl && shift => () => SearchAsync("text", Editor.SelectedText.Length < 100 ? Editor.SelectedText : ""),
            Key.B when ctrl => () => NavigateDefinitionAsync(),
            Key.F12 when !ctrl => () => NavigateDefinitionAsync(),
            Key.F5 => RefreshWorkspaceAsync,
            Key.Enter when ctrl => CommitAsync,
            Key.W when ctrl && _active != null => () => CloseDocumentAsync(_active),
            Key.Left when alt => GoBackAsync,
            _ => null
        };
        if (action != null) { e.Handled = true; await Guard(action); return; }
        if (ctrl && e.Key == Key.K) { CommitMessage.Focus(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.F12) { ToggleFocusMode(); e.Handled = true; }
        else if (ctrl && e.Key == Key.H && _active != null) { ReplaceText(); e.Handled = true; }
        else if (ctrl && e.Key == Key.G) { GoToLine(); e.Handled = true; }
        else if (ctrl && alt && e.Key == Key.L && _active != null) { NormalizeIndentation(); e.Handled = true; }
        else if (ctrl && e.Key == Key.D && _active != null && Editor.IsKeyboardFocusWithin) { DuplicateLine(); e.Handled = true; }
        else if (ctrl && e.Key == Key.OemQuestion && _active != null && Editor.IsKeyboardFocusWithin) { ToggleComment(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Tab && _documents.Count > 1) { e.Handled = true; var index = (_documents.IndexOf(_active!) + (shift ? _documents.Count - 1 : 1)) % _documents.Count; await Guard(() => OpenFileAsync(Relative(_documents[index].File.Path))); }
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
        _focusMode = !_focusMode;
        if (_focusMode)
        {
            _explorerWidth = ExplorerColumn.ActualWidth; _changesWidth = ChangesColumn.ActualWidth;
            ExplorerColumn.MinWidth = ChangesColumn.MinWidth = 0;
            ExplorerColumn.Width = ChangesColumn.Width = new GridLength(0);
        }
        else
        {
            ExplorerColumn.MinWidth = 160; ChangesColumn.MinWidth = 245;
            ExplorerColumn.Width = new GridLength(_explorerWidth); ChangesColumn.Width = new GridLength(_changesWidth);
        }
        SetStatus("Ctrl+Shift+F12 切换侧边栏 · 当前为" + (_focusMode ? "专注模式" : "完整工作台"));
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
        var picker = new OpenFolderDialog { Title = "选择本地项目 / Git 仓库", Multiselect = false };
        if (_workspace != null) picker.InitialDirectory = _workspace.Root;
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
        _workspace = snapshot; FileCount.Text = snapshot.Files.Length.ToString("N0"); PopulateTree(FileTree, "");
        SpringList.ItemsSource = snapshot.Spring; MavenList.ItemsSource = snapshot.Maven;
        IndexLabel.Text = $"{snapshot.Symbols.Count:N0} 个代码符号 · {snapshot.Maven.Count} 个 Maven 模块" + (snapshot.Limited ? "\n索引达到上限" : "\n按需索引 · 无常驻 JVM");
        // Reload clean buffers changed by Git/AI tools, keeping dirty buffers intact.
        foreach (var doc in _documents.Where(d => !d.Dirty).ToArray())
        {
            if (!File.Exists(doc.File.Path)) continue;
            var fresh = await FileService.ReadAsync(doc.File.Path, token);
            if (fresh.Hash == doc.File.Hash || doc.Dirty) continue;
            _loadingDocument = true; doc.File = fresh; doc.Document.Text = fresh.Text; _loadingDocument = false;
        }
        await RefreshGitAsync(); SetStatus("索引与 Git 状态已刷新");
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
        _command?.Cancel(); _workspaceLoad?.Cancel(); _diffLoad?.Cancel(); _memoryTimer.Stop();
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
    private async void ChangeSelected(object sender, SelectionChangedEventArgs e) { if (!_refreshingGit && ChangesList.SelectedItem is GitChange change) await Guard(() => ShowDiffAsync(change)); }
    private async void ChangeDoubleClick(object sender, MouseButtonEventArgs e) { if (ChangesList.SelectedItem is GitChange change) await Guard(() => OpenFileAsync(change.Path)); }
    private async void DiffModeClick(object sender, RoutedEventArgs e) { if (ChangesList.SelectedItem is GitChange change) await Guard(() => ShowDiffAsync(change)); }
    private async void EditChangeClick(object sender, RoutedEventArgs e) { if (ChangesList.SelectedItem is GitChange change) await Guard(() => OpenFileAsync(change.Path)); }
    private void PreviousDiffClick(object sender, RoutedEventArgs e) => MoveDiff(-1);
    private void NextDiffClick(object sender, RoutedEventArgs e) => MoveDiff(1);
    private async void StageClick(object sender, RoutedEventArgs e) => await Guard(() => GitOperationAsync("暂存", async git => { var paths = SelectedPaths(); if (paths.Length > 0) await git.StageAsync(paths); }));
    private async void UnstageClick(object sender, RoutedEventArgs e) => await Guard(() => GitOperationAsync("取消暂存", async git => { var paths = SelectedPaths(); if (paths.Length > 0) await git.UnstageAsync(paths); }));
    private async void StageAllClick(object sender, RoutedEventArgs e) => await Guard(() => GitOperationAsync("全部暂存", async git => { if (_gitStatus?.Changes.Count > 0) await git.StageAsync(["."]); }));
    private async Task CommitAsync() => await GitOperationAsync("提交", async git => { await git.CommitAsync(CommitMessage.Text); CommitMessage.Clear(); });
    private async void CommitClick(object sender, RoutedEventArgs e) => await Guard(CommitAsync);
    private async void PullClick(object sender, RoutedEventArgs e) => await Guard(() => RunGitCommandAsync(["pull", "--ff-only"]));
    private async void PushClick(object sender, RoutedEventArgs e) => await Guard(() => RunGitCommandAsync(["push"]));
    private async void DiscardClick(object sender, RoutedEventArgs e) => await Guard(() => GitOperationAsync("丢弃改动", async git =>
    {
        var changes = ChangesList.SelectedItems.Cast<GitChange>().ToArray();
        if (changes.Length == 0 || !Dialogs.Confirm(this, "永久丢弃以下文件的工作区修改？未跟踪文件将被删除，暂存区内容会保留。\n\n" + string.Join('\n', changes.Select(c => c.Path)))) return;
        foreach (var change in changes)
        {
            if (change.IsUntracked) File.Delete(FileService.SafePath(git.Root, change.Path));
            else (await git.RunAsync(["restore", "--worktree", "--", change.Path])).EnsureSuccess();
        }
        await RefreshWorkspaceAsync();
    }));
    private async void UseOursClick(object sender, RoutedEventArgs e) => await Guard(() => ResolveNextAsync("ours"));
    private async void UseTheirsClick(object sender, RoutedEventArgs e) => await Guard(() => ResolveNextAsync("theirs"));
    private async void UseBothClick(object sender, RoutedEventArgs e) => await Guard(() => ResolveNextAsync("both"));
    private async Task ResolveNextAsync(string choice)
    {
        if (ChangesList.SelectedItem is not GitChange { IsConflict: true } change) return;
        await OpenFileAsync(change.Path);
        var blocks = ConflictParser.Parse(Editor.Text);
        if (blocks.Count == 0) { SetStatus("没有文本冲突标记；请检查结果，完成后保存并暂存。二进制/删除冲突请使用 Git 命令。 "); return; }
        var block = blocks[0];
        Editor.Document.Replace(block.Start, block.Length, choice == "ours" ? block.Ours : choice == "theirs" ? block.Theirs : block.Ours + block.Theirs);
        Editor.CaretOffset = Math.Min(block.Start, Editor.Document.TextLength); Editor.ScrollToLine(Editor.TextArea.Caret.Line);
        SetStatus($"已处理 1 处冲突，剩余 {blocks.Count - 1} 处。结果尚未保存。可 Ctrl+Z 撤销。");
    }
    private async void ResolveClick(object sender, RoutedEventArgs e) => await Guard(() => GitOperationAsync("保存冲突结果并暂存", async git =>
    {
        if (ChangesList.SelectedItem is not GitChange change) return;
        await OpenFileAsync(change.Path);
        if (ConflictParser.Parse(Editor.Text).Count > 0) throw new InvalidOperationException("仍有未解决的冲突标记，请先完成所有冲突。");
        await SaveAsync();
        if (_active?.Dirty == true) return;
        await git.StageAsync([change.Path]);
    }));

    private async void ViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Views) || _switchingView || !IsLoaded) return;
        if (Views.SelectedIndex == 2) { _switchingView = true; try { await Guard(LoadHistoryAsync); } finally { _switchingView = false; } }
    }
    private async Task LoadHistoryAsync() { if (_git != null) HistoryEditor.Text = await _git.LogAsync(); }
    private async void HistoryClick(object sender, RoutedEventArgs e) => await Guard(LoadHistoryAsync);
    private async void HistoryDoubleClick(object sender, MouseButtonEventArgs e) => await Guard(async () =>
    {
        if (_git == null) return;
        var line = HistoryEditor.Document.GetText(HistoryEditor.Document.GetLineByOffset(HistoryEditor.CaretOffset));
        var hash = Regex.Match(line, @"\b[0-9a-f]{7,40}\b").Value;
        if (hash.Length > 0) { CommitEditor.Text = await _git.ShowCommitAsync(hash); SetStatus("提交 " + hash + " · 在 Git 菜单可执行 cherry-pick、revert、tag 等操作"); }
    });
    private async void SpringDoubleClick(object sender, MouseButtonEventArgs e) { if (SpringList.SelectedItem is SearchHit hit) await Guard(() => OpenFileAsync(hit.Path, hit.Line)); }
    private async void MavenDoubleClick(object sender, MouseButtonEventArgs e) { if (MavenList.SelectedItem is MavenModule module) await Guard(() => OpenFileAsync(module.Path)); }
    private void MavenSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MavenList.SelectedItem is not MavenModule module) return;
        ShowOutput("Maven · " + module.Artifact, $"{module.Group}:{module.Artifact}:{module.Version}\nPOM: {module.Path}\n模块: {string.Join(", ", module.Modules)}\n\n依赖（不下载、不解析远程 BOM）：\n" + string.Join('\n', module.Dependencies) + (module.Error == null ? "" : "\n" + module.Error));
    }
    private void ShowOutput(string title, string output) { OutputPanel.Visibility = Visibility.Visible; OutputTitle.Text = title; OutputEditor.Text = output; }
    private void CancelCommandClick(object sender, RoutedEventArgs e) => _command?.Cancel();
    private void CloseOutputClick(object sender, RoutedEventArgs e) => OutputPanel.Visibility = Visibility.Collapsed;

    private void HelpClick(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Ctrl+O  打开项目\nShift × 2 / Ctrl+P  全局文件与符号搜索\nCtrl+N / Ctrl+Alt+Shift+N  类型、方法搜索\nCtrl+Shift+F  全项目内容搜索（支持正则）\nCtrl+F / Ctrl+H  文件内查找 / 替换\nCtrl+B / F12 / Ctrl+鼠标  项目内符号定义\nAlt+Left  返回跳转位置\nCtrl+G  跳转到行:列\nCtrl+S  保存并匹配 .editorconfig\nCtrl+Alt+L  按风格统一缩进空白\nCtrl+D  复制当前行\nCtrl+/  切换行注释\nCtrl+W / Ctrl+Tab  关闭 / 切换文档\nCtrl+K  聚焦提交说明\nCtrl+Enter  提交暂存区\nF5  刷新文件、源码索引与 Git\nCtrl+滚轮  字体缩放\n\nGit 菜单包含分支、stash、合并、rebase、remotes 和完整 Git 参数入口。\n默认不启动 JVM、不自动构建、不上传源码。Java 导航使用源码符号索引，不提供编译器级类型分析。",
        "Idea Lite · 快捷键", MessageBoxButton.OK, MessageBoxImage.Information);
}

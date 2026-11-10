using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit.Document;
using IdeaLite.Core;

namespace IdeaLite;

public sealed record ChangeItem(GitChange Change, string Group, bool Staged, CommitChoice Choice)
{
    public bool CanInclude => !Staged && !Change.IsConflict;
    public string StatusCode => Staged ? Change.Index.ToString() : Change.IsConflict ? "!" : Change.WorkTree.ToString();
    public string Icon => MainWindow.FileIcon(Change.Path);
    public string Color => Staged ? Change.Index switch { 'D' => "#F77669", 'A' => "#C3E887", _ => "#80CBC4" } : FileColor(Change);
    internal static string FileColor(GitChange change) => change.IsConflict || change.IsUntracked || change.Index == 'D' || change.WorkTree == 'D'
        ? "#F77669" : change.Index == 'A' ? "#C3E887" : "#80CBC4";
}

public sealed class CommitChoice : System.ComponentModel.INotifyPropertyChanged
{
    private bool _included;
    public bool Included
    {
        get => _included;
        set { if (_included == value) return; _included = value; PropertyChanged?.Invoke(this, new(nameof(Included))); }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow
{
    private sealed record DiffPreview(string Path, bool Staged, GitChange? Change, GitRevision? Revision, RevisionFile? File)
    {
        public string Description => Revision == null ? $"{Path} · {UiText.Current.Translate(Staged ? "已暂存" : "工作区")}" : $"{Path} · {Revision.ShortHash}";
        public static DiffPreview Local(GitChange change, bool staged) => new(change.Path, staged, change, null, null);
        public static DiffPreview History(GitRevision revision, RevisionFile file) => new(file.Path, false, null, revision, file);
    }
    private DiffPreview? _preview;
    private GitDiff? _renderedDiff;
    private bool _renderedIgnore;
    private DocumentState? _diffDocument;
    private readonly DispatcherTimer _diffUpdateTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _updatingDiff;
    private readonly Dictionary<string, CommitChoice> _commitChoices = new(StringComparer.Ordinal);
    private bool _bulkSelecting;
    private CancellationTokenSource? _historyLoad, _detailLoad;
    private IReadOnlyList<GitRef> _references = [];
    private IReadOnlyList<GitRevision> _revisions = [];
    private string? _logRef;
    private bool _buildingRefs;
    private string? _amendDraft, _amendMessage;
    private int _amendRequest;

    private void ResetReviewState()
    {
        ResetAmendMessage();
        _historyLoad?.Cancel(); _detailLoad?.Cancel(); _diffLoad?.Cancel();
        DetachDiffDocument();
        _preview = null; _renderedDiff = null; _diffScroll.Reset(); _commitChoices.Clear(); _references = []; _revisions = []; _logRef = null;
        _buildingRefs = true; LogRefs.Items.Clear(); _buildingRefs = false;
        LogList.ItemsSource = null; HistoryFiles.Items.Clear(); RevisionDetails.Clear(); OutputEditor.Clear();
        GitSummary.Visibility = Visibility.Visible; UiText.Set(GitSummary, "Text", "正在读取改动…");
        LogHint.Text = ""; RefFilter.Clear(); LogFilter.Clear();
    }
    private void BindChanges(GitStatus status)
    {
        var selection = ChangesList.SelectedItems.Cast<ChangeItem>().Select(c => (c.Change.Path, c.Staged)).ToHashSet();
        var entries = new List<ChangeItem>();
        foreach (var removed in _commitChoices.Keys.Where(path => !status.Changes.Any(c => c.Path == path)).ToArray()) _commitChoices.Remove(removed);
        foreach (var change in status.Changes)
        {
            if (!_commitChoices.TryGetValue(change.Path, out var choice))
            {
                choice = new CommitChoice();
                choice.PropertyChanged += (_, _) => { if (!_bulkSelecting) UpdateCommitSelection(); };
                _commitChoices.Add(change.Path, choice);
            }
            if (change.IsStaged) entries.Add(new(change, "已暂存", true, new CommitChoice()));
            if (change.HasWorkingChanges || change.IsConflict)
                entries.Add(new(change, change.IsConflict ? "合并冲突" : change.IsUntracked ? "未版本化文件" : "未暂存", false, choice));
        }
        _bulkSelecting = true;
        try { foreach (var change in status.Changes.Where(change => change.IsConflict || !change.HasWorkingChanges)) _commitChoices[change.Path].Included = false; }
        finally { _bulkSelecting = false; }

        var view = new ListCollectionView(entries);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChangeItem.Group)));
        ChangesList.ItemsSource = view;
        foreach (var entry in entries)
            if (selection.Contains((entry.Change.Path, entry.Staged)) ||
                selection.Contains((entry.Change.Path, !entry.Staged)) && !(entry.Change.IsStaged && entry.Change.HasWorkingChanges))
                ChangesList.SelectedItems.Add(entry);
        UiText.Set(GitSummary, "Text", entries.Count == 0 ? "工作区没有改动" : "");
        GitSummary.Visibility = Visible(entries.Count == 0);
        UpdateCommitSelection();
        UiText.Set(CommitRail, "ToolTip", $"提交 Commit · Alt+0 · {status.Changes.Count} 个改动");
    }
    private void UpdateCommitSelection()
    {
        if (SelectAllChanges == null) return;
        var eligible = (_gitStatus?.Changes ?? []).Where(c => c.HasWorkingChanges && !c.IsConflict).Select(c => _commitChoices[c.Path]).ToArray();
        int count = eligible.Count(c => c.Included);
        SelectAllChanges.IsChecked = count == 0 ? false : count == eligible.Length ? true : null;
        UiText.Set(SelectionSummary, "Text", $"已选择 {count} / {eligible.Length} 个文件");
        bool indexOnly = UsesStagedCommit();
        UiText.Set(CommitSelectedButton, "Content", indexOnly ? "提交暂存区" : "提交");
        UiText.Set(CommitPushButton, "Content", indexOnly ? "提交暂存区并推送…" : "提交并推送…");
        bool enabled = count > 0 || AmendCheck.IsChecked == true || indexOnly;
        CommitSelectedButton.IsEnabled = CommitPushButton.IsEnabled = enabled && _aiGeneration == null;
    }
    private void SelectAllChangesClick(object sender, RoutedEventArgs e)
    {
        _bulkSelecting = true;
        try { foreach (var change in (_gitStatus?.Changes ?? []).Where(c => c.HasWorkingChanges && !c.IsConflict)) _commitChoices[change.Path].Included = SelectAllChanges.IsChecked == true; }
        finally { _bulkSelecting = false; }
        UpdateCommitSelection();
    }
    private bool UsesStagedCommit() => AmendCheck.IsChecked != true && !_commitChoices.Values.Any(choice => choice.Included) &&
        _gitStatus?.Changes.Any(change => change.IsStaged) == true;
    private async void AmendChanged(object sender, RoutedEventArgs e) => await Guard(UpdateAmendMessageAsync);
    private void ResetAmendMessage() { _amendRequest++; _amendDraft = _amendMessage = null; }
    private async Task UpdateAmendMessageAsync()
    {
        int request = ++_amendRequest;
        UpdateCommitSelection();
        if (AmendCheck.IsChecked != true)
        {
            if (_amendMessage != null && CommitMessage.Text == _amendMessage) CommitMessage.Text = _amendDraft ?? "";
            _amendDraft = _amendMessage = null;
            return;
        }
        var git = _git;
        if (git == null) return;
        string draft = CommitMessage.Text;
        var message = await git.LastCommitMessageAsync();
        if (request != _amendRequest || git != _git || AmendCheck.IsChecked != true) return;
        if (message == null)
        {
            AmendCheck.IsChecked = false; UpdateCommitSelection();
            SetStatus("尚无可修改的提交，请先创建第一次提交。"); return;
        }
        _amendDraft = draft; _amendMessage = message;
        if (CommitMessage.Text == draft) CommitMessage.Text = message;
    }
    private async void StagedDiffClick(object sender, RoutedEventArgs e)
    {
        if (ChangesList.SelectedItem is ChangeItem item && item.Change.IsStaged)
            await Guard(() => ShowPreviewAsync(DiffPreview.Local(item.Change, true)));
    }

    private async Task OpenSelectedChangeAsync()
    {
        if (ChangesList.SelectedItem is not ChangeItem item) return;
        if (item.Change.IsConflict) { await OpenMergeAsync(item.Change); return; }
        await ShowPreviewAsync(DiffPreview.Local(item.Change, item.Staged));
    }
    private async Task OpenSelectedSourceAsync()
    {
        if (ChangesList.SelectedItem is ChangeItem item) await OpenFileAsync(item.Change.Path);
    }
    private Task ClosePreviewAsync() => CloseTabsAsync([], closeDiff: true);

    private void DetachDiffDocument()
    {
        _diffUpdateTimer.Stop();
        if (_diffDocument is { } document)
        {
            NewEditor.Document = new TextDocument();
            if (!document.HasTab)
            {
                // The Diff can be the only visible tab for a shared working-tree buffer.
                if (document.Dirty || document.Saving) document.HasTab = true;
                else _documents.Remove(document);
            }
        }
        _diffDocument = null;
        NewEditor.IsReadOnly = true;
        _diffUsages.Set(NewEditor, null, null);
    }

    private async Task ShowPreviewAsync(DiffPreview preview)
    {
        var git = _git;
        if (git == null) return;
        var request = ++_openRequest;
        _diffUpdateTimer.Stop();
        _diffLoad?.Cancel(); _diffLoad?.Dispose(); _diffLoad = new();
        var token = _diffLoad.Token;
        var diff = preview.Revision != null && preview.File != null
            ? await git.RevisionDiffAsync(preview.Revision, preview.File, token)
            : await git.DiffAsync(preview.Change!, preview.Staged, token);
        token.ThrowIfCancellationRequested();
        if (diff.OldText.Contains('\0') || diff.NewText.Contains('\0')) throw new IOException("二进制文件无法显示文本差异。");
        DocumentState? document = null;
        if (preview.Revision == null && !preview.Staged)
        {
            var path = FileService.SafePath(git.Root, preview.Path);
            if (File.Exists(path))
            {
                document = await GetDocumentAsync(path, request, token);
                if (document is { Dirty: false, Saving: false } && document.File.Text != diff.NewText)
                {
                    var fresh = await FileService.ReadAsync(path, token);
                    token.ThrowIfCancellationRequested();
                    ReloadCleanDocument(document, fresh);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        bool ignore = IgnoreWhitespace.IsChecked == true;
        bool sameView = _preview?.Path == preview.Path && _preview.Staged == preview.Staged &&
            _preview.Revision == preview.Revision && _diffDocument == document;
        if (document != null) diff = diff with { NewText = document.Document.Text };
        if (sameView && _renderedDiff == diff && _renderedIgnore == ignore)
        {
            _preview = preview;
            bool entering = !_showDiff;
            SetSurface(true);
            if (entering) FocusEditor();
            return;
        }
        var model = await BuildPreviewModelAsync(diff, ignore, token);
        token.ThrowIfCancellationRequested();
        if (document != null && !_documents.Contains(document)) return;
        if (model == null && document == null)
            throw new IOException("差异超过视图上限（两侧共 100 万字符或 4 万行）。可在项目终端用 Git 查看。");
        RenderPreview(preview, document, diff, model, ignore, !sameView);
        if (document != null && document.Document.Text != diff.NewText) QueueDiffUpdate();
    }

    private static Task<SideBySideDiffModel?> BuildPreviewModelAsync(GitDiff diff, bool ignore, CancellationToken token)
    {
        // The editable buffer can grow beyond the comparison limit while the user is typing.
        if (diff.OldText.Length + diff.NewText.Length > 1_000_000 ||
            diff.OldText.Count(c => c == '\n') + diff.NewText.Count(c => c == '\n') > 40_000)
            return Task.FromResult<SideBySideDiffModel?>(null);
        return Task.Run<SideBySideDiffModel?>(() => EditorSupport.BuildDiff(diff.OldText, diff.NewText, ignore), token);
    }

    private void QueueDiffUpdate()
    {
        _diffUpdateTimer.Stop();
        if (_showDiff) _diffUpdateTimer.Start();
    }

    private async Task UpdateEditableDiffAsync()
    {
        _diffUpdateTimer.Stop();
        if (_updatingDiff || !_showDiff || _diffDocument == null || _preview == null || _renderedDiff == null) return;
        var document = _diffDocument;
        var preview = _preview;
        var baseline = _renderedDiff;
        var draft = document.Document.Text;
        if (draft == baseline.NewText) return;
        var diff = baseline with { NewText = draft };
        var token = _diffLoad?.Token ?? default;
        bool ignore = _renderedIgnore;
        _updatingDiff = true;
        try
        {
            var model = await BuildPreviewModelAsync(diff, ignore, token);
            if (!token.IsCancellationRequested && _showDiff && _diffDocument == document &&
                ReferenceEquals(_renderedDiff, baseline) && document.Document.Text == draft)
                RenderPreview(preview, document, diff, model, ignore, false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _updatingDiff = false;
            if (!token.IsCancellationRequested && _showDiff && _diffDocument != null &&
                _diffDocument.Document.Text != _renderedDiff?.NewText)
                QueueDiffUpdate();
        }
    }

    private void RenderPreview(DiffPreview preview, DocumentState? document, GitDiff diff, SideBySideDiffModel? model, bool ignore, bool navigate)
    {
        ClearDefinitionHover();
        _preview = preview;
        _renderedDiff = diff; _renderedIgnore = ignore;
        var oldLines = model?.OldText.Lines ?? [];
        var newLines = model?.NewText.Lines ?? [];
        if (model == null)
        {
            OldEditor.Text = diff.OldText;
            EditorSupport.Highlight(OldEditor, preview.File?.OldPath ?? preview.Path);
            EditorSupport.SetDiffDecorations(OldEditor, [], true, aligned: false);
        }
        else EditorSupport.SetDiffText(OldEditor, oldLines, preview.File?.OldPath ?? preview.Path, true);
        bool newDocument = _diffDocument != document;
        if (newDocument) DetachDiffDocument();
        _diffDocument = document;
        _diffScroll.SetRightLines(document == null ? null : newLines.Select(line => line.Position).ToArray());
        OldOverview.SetChanges(oldLines, true);
        if (document == null)
        {
            EditorSupport.SetDiffText(NewEditor, newLines, preview.Path, false);
            NewOverview.SetChanges(newLines, false);
        }
        else
        {
            if (NewEditor.Document != document.Document) NewEditor.Document = document.Document;
            NewEditor.IsReadOnly = false;
            NewEditor.Options.ConvertTabsToSpaces = !document.File.Style.UseTabs;
            NewEditor.Options.IndentationSize = document.File.Style.IndentSize;
            EditorSupport.Highlight(NewEditor, preview.Path);
            var actualLines = newLines.Where(line => line.Position.HasValue).ToArray();
            EditorSupport.SetDiffDecorations(NewEditor, actualLines, false, aligned: false);
            NewOverview.SetChanges(LineChanges.Aligned(newLines, false).Select(change => change with
            {
                FirstLine = _diffScroll.RightLine(change.FirstLine),
                LastLine = _diffScroll.RightLine(change.LastLine)
            }).ToArray());
            if (newDocument) _diffUsages.Set(NewEditor, _workspace, preview.Path);
        }
        UiText.Set(OldLabel, "Text", () => TranslateDiffLabel(diff.OldLabel));
        UiText.Set(NewLabel, "Text", () => TranslateDiffLabel(diff.NewLabel) +
            (document == null ? "" : " · " + UiText.Current.Translate("可编辑 · Ctrl+S 保存")));
        _diffLines = newLines.Select((line, i) => (line, i)).Where(x => x.line.Type != ChangeType.Unchanged &&
            (x.i == 0 || newLines[x.i - 1].Type == ChangeType.Unchanged)).Select(x => x.i + 1).ToArray();
        _diffIndex = navigate ? -1 : Math.Min(_diffIndex, _diffLines.Length - 1);
        UiText.Set(DiffSummary, "Text", model == null ? "差异超过视图上限" :
            _diffLines.Length == 0 ? "没有文本差异" : $"{_diffLines.Length} 处差异");
        bool entering = !_showDiff || navigate;
        if (entering) SetSurface(true);
        if (navigate)
        {
            SetStatus(preview.Description);
            MoveDiff(1);
        }
        else if (document != null) _diffScroll.AlignFromRight();
        if (entering) FocusEditor();
    }

    private static string TranslateDiffLabel(string label)
    {
        var parts = label.Split("   ", 2, StringSplitOptions.None);
        return UiText.Current.Translate(parts[0]) + (parts.Length == 2 ? "   " + parts[1] : "");
    }

    private async Task OpenMergeAsync(GitChange change)
    {
        var git = _git;
        if (git == null) return;
        var path = FileService.SafePath(git.Root, change.Path);
        if (!File.Exists(path)) throw new IOException("该冲突涉及删除。请通过 Git 菜单或项目终端选择保留文件或删除。");
        var existing = _documents.FirstOrDefault(d => string.Equals(d.File.Path, path, StringComparison.OrdinalIgnoreCase));
        var baseline = existing is { Dirty: true } ? existing.File : await FileService.ReadAsync(path);
        var draft = existing is { Dirty: true } ? existing.Document.Text : baseline.Text;
        var sides = await git.ConflictSidesAsync(change.Path);
        if (sides.OldText.Contains('\0') || sides.NewText.Contains('\0')) throw new IOException("二进制冲突请使用项目终端处理。");
        async Task ApplyResult(string result)
        {
            baseline = await FileService.SaveAsync(baseline, result);
            if (existing != null)
            {
                _loadingDocument = true;
                try { existing.File = baseline; existing.Document.Text = baseline.Text; existing.Dirty = false; }
                finally { _loadingDocument = false; }
                RebuildTabs();
            }
            await git.StageAsync([change.Path]);
        }
        if (git != _git) return;
        var merge = new MergeWindow(this, change.Path, sides, draft, baseline.Style, ApplyResult);
        if (merge.ShowDialog() == true)
        {
            await RefreshGitAsync();
            SetStatus("已应用合并结果并暂存 " + change.Path);
        }
    }

    private async Task LoadHistoryAsync()
    {
        var git = _git;
        _historyLoad?.Cancel(); _historyLoad?.Dispose(); _historyLoad = new();
        var token = _historyLoad.Token;
        if (git == null) { UiText.Set(LogHint, "Text", "打开 Git 仓库后查看日志"); return; }
        var reference = _logRef;
        var refsTask = git.ReferencesAsync(token);
        var logTask = git.RevisionsAsync(reference, token);
        await Task.WhenAll(refsTask, logTask);
        token.ThrowIfCancellationRequested();
        _references = await refsTask;
        _revisions = await logTask;
        RenderRefs();
        FilterLog();
    }
    private void RenderRefs()
    {
        if (LogRefs == null) return;
        _buildingRefs = true;
        try
        {
            LogRefs.Items.Clear();
            var all = new TreeViewItem { Header = FileHeader(UiText.Current.Translate("所有分支"), "Git", UiText.Current.Translate("所有本地和远程引用")) };
            LogRefs.Items.Add(all);
            if (_logRef == null) all.IsSelected = true;
            var head = new TreeViewItem { Header = FileHeader(UiText.Current.Translate("HEAD (当前分支)"), "Tag", UiText.Current.Translate("当前分支的历史")), Tag = "HEAD" };
            LogRefs.Items.Add(head);
            if (_logRef == "HEAD") head.IsSelected = true;
            var filter = RefFilter.Text.Trim();
            foreach (var group in _references.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(1000).GroupBy(r => r.Group))
            {
                var node = new TreeViewItem { Header = group.Key, IsExpanded = group.Key == "本地" || filter.Length > 0 }.Localize("Header");
                LogRefs.Items.Add(node);
                foreach (var reference in group)
                {
                    var item = new TreeViewItem { Header = FileHeader(reference.Name, reference.Current ? "Tag" : "Git", reference.FullName), Tag = reference };
                    node.Items.Add(item);
                    if (reference.FullName == _logRef) { node.IsExpanded = true; item.IsSelected = true; }
                }
            }
        }
        finally { _buildingRefs = false; }
    }
    private async void LogRefSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_buildingRefs || LogRefs.SelectedItem is not TreeViewItem item || item.Items.Count > 0) return;
        var value = item.Tag is GitRef reference ? reference.FullName : item.Tag as string;
        if (_logRef == value) return;
        _logRef = value;
        await Guard(LoadHistoryAsync);
    }
    private void RefFilterChanged(object sender, TextChangedEventArgs e) => RenderRefs();
    private void LogFilterChanged(object sender, TextChangedEventArgs e) => FilterLog();

    private void FilterLog()
    {
        if (LogList == null) return;
        var previous = (LogList.SelectedItem as LogRow)?.Revision.Hash;
        var query = LogFilter.Text.Trim();
        var rows = _revisions.Where(r => r.Subject.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            r.Hash.StartsWith(query, StringComparison.OrdinalIgnoreCase) || r.Author.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var graph = LogRow.Build(rows, query.Length > 0);
        LogList.ItemsSource = graph;
        LogList.SelectedItem = graph.FirstOrDefault(r => r.Revision.Hash == previous) ?? graph.FirstOrDefault();
        UiText.Set(LogHint, "Text", graph.Count == 0 ? (query.Length > 0 ? "没有匹配的提交" : "暂无提交") : "");
    }
    private async void LogSelectionChanged(object sender, SelectionChangedEventArgs e) => await Guard(async () =>
    {
        _detailLoad?.Cancel(); _detailLoad?.Dispose(); _detailLoad = new();
        var token = _detailLoad.Token;
        HistoryFiles.Items.Clear(); RevisionDetails.Text = ""; UiText.Set(HistoryFilesTitle, "Text", "更改的文件");
        if (LogList.SelectedItem is not LogRow row || _git == null) return;
        var revision = row.Revision;
        var filesTask = _git.RevisionFilesAsync(revision, token);
        var messageTask = _git.RunAsync(["show", "--no-patch", "--format=%B", revision.Hash, "--"], token);
        await Task.WhenAll(filesTask, messageTask);
        token.ThrowIfCancellationRequested();
        var files = await filesTask;
        var message = (await messageTask).EnsureSuccess().Output.TrimEnd();
        RevisionDetails.Text = $"{message}\n\n{revision.ShortHash}   {revision.Author}\n{revision.Date.ToLocalTime():yyyy-MM-dd HH:mm}   {revision.References}";
        UiText.Set(HistoryFilesTitle, "Text", $"{files.Count} 个更改文件");
        // Compact directories so the changed file is visible even in a short bottom tool window.
        foreach (var group in files.Take(1500).GroupBy(file => Path.GetDirectoryName(file.Path)?.Replace('\\', '/') ?? ""))
        {
            ItemsControl parent = HistoryFiles;
            if (group.Key.Length > 0)
            {
                var header = FileHeader(group.Key, "Folder", group.Key);
                var label = (TextBlock)header.Children[1];
                label.MaxWidth = 165; label.TextTrimming = TextTrimming.CharacterEllipsis;
                var folder = new TreeViewItem { Header = header, IsExpanded = true };
                HistoryFiles.Items.Add(folder); parent = folder;
            }
            foreach (var file in group)
            {
                var header = FileHeader(file.Name, FileIcon(file.Path), file.Path);
                ((TextBlock)header.Children[1]).Foreground = file.Status switch
                {
                    'A' => System.Windows.Media.Brushes.DarkSeaGreen,
                    'D' => System.Windows.Media.Brushes.LightCoral,
                    _ => System.Windows.Media.Brushes.LightSteelBlue
                };
                parent.Items.Add(new TreeViewItem { Header = header, Tag = file });
            }
        }

        if (files.Count > 1500) UiText.Set(HistoryFilesTitle, "Text", $"{files.Count} 个更改文件（显示前 1,500 个）");
    });
    private async Task OpenHistoryFileAsync()
    {
        if (LogList.SelectedItem is LogRow row && HistoryFiles.SelectedItem is TreeViewItem { Tag: RevisionFile file })
            await ShowPreviewAsync(DiffPreview.History(row.Revision, file));
    }
    private async void HistoryFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TreeItemAt(e.OriginalSource) is { Tag: RevisionFile }) await Guard(OpenHistoryFileAsync);
    }
    private async void HistoryFileDiffClick(object sender, RoutedEventArgs e) => await Guard(OpenHistoryFileAsync);
    private async void LogDoubleClick(object sender, MouseButtonEventArgs e) => await Guard(async () =>
    {
        if (_git != null && LogList.SelectedItem is LogRow row)
            ShowOutput("git show " + row.Revision.ShortHash, await _git.ShowCommitAsync(row.Revision.Hash));
    });

    private void LogRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt<ListBoxItem>(LogList, e.OriginalSource) is { } item) item.IsSelected = true;
        else LogList.SelectedIndex = -1;
    }

    private async void LogCherryPickClick(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is LogRow row) await Guard(() => CherryPickRevisionAsync(row.Revision));
    }

    private async Task CherryPickRevisionAsync(GitRevision revision)
    {
        var args = new List<string> { "cherry-pick" };
        if (revision.Parents.Length > 1)
        {
            var parents = string.Join('\n', revision.Parents.Select((hash, index) => $"{index + 1}  {hash[..8]}"));
            var value = Dialogs.Prompt(this, "Cherry-pick 合并提交", UiText.Current.Translate("请选择用于计算差异的父提交编号。") + "\n\n" + parents);
            if (value == null) return;
            if (!int.TryParse(value, out int parent) || parent < 1 || parent > revision.Parents.Length)
            {
                SetStatus("父提交编号无效。");
                return;
            }
            args.AddRange(["--mainline", parent.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        else if (!Dialogs.Confirm(this, UiText.Current.Translate("将这个提交应用到当前分支？") + $"\n\n{revision.ShortHash} {revision.Subject}")) return;
        args.AddRange(["--", revision.Hash]);
        await RunGitCommandAsync(args.ToArray());
    }
}

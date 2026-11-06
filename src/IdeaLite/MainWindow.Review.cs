using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using IdeaLite.Core;

namespace IdeaLite;

public sealed record ChangeItem(GitChange Change, string Group, bool Staged, CommitChoice Choice)
{
    public bool CanInclude => !Change.IsConflict;
    public string Icon => MainWindow.FileIcon(Change.Path);
    public string Color => FileColor(Change);
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
    private readonly Dictionary<string, CommitChoice> _commitChoices = new(StringComparer.Ordinal);
    private bool _bulkSelecting;
    private CancellationTokenSource? _historyLoad, _detailLoad;
    private IReadOnlyList<GitRef> _references = [];
    private IReadOnlyList<GitRevision> _revisions = [];
    private string? _logRef;
    private bool _buildingRefs;

    private void ResetReviewState()
    {
        _historyLoad?.Cancel(); _detailLoad?.Cancel(); _diffLoad?.Cancel();
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
            if (change.IsConflict) choice.Included = false;
            var group = change.IsConflict ? "合并冲突" : change.IsUntracked ? "未版本化文件" : "更改";
            entries.Add(new(change, group, change.IsStaged && !change.HasWorkingChanges, choice));
        }

        var view = new ListCollectionView(entries);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChangeItem.Group)));
        ChangesList.ItemsSource = view;
        foreach (var entry in entries)
            if (selection.Contains((entry.Change.Path, entry.Staged))) ChangesList.SelectedItems.Add(entry);
        UiText.Set(GitSummary, "Text", entries.Count == 0 ? "工作区没有改动" : "");
        GitSummary.Visibility = Visible(entries.Count == 0);
        UpdateCommitSelection();
        UiText.Set(CommitRail, "ToolTip", $"提交 Commit · Alt+0 · {status.Changes.Count} 个改动");
    }
    private void UpdateCommitSelection()
    {
        if (SelectAllChanges == null) return;
        var eligible = (_gitStatus?.Changes ?? []).Where(c => !c.IsConflict).Select(c => _commitChoices[c.Path]).ToArray();
        int count = eligible.Count(c => c.Included);
        SelectAllChanges.IsChecked = count == 0 ? false : count == eligible.Length ? true : null;
        UiText.Set(SelectionSummary, "Text", $"已选择 {count} / {eligible.Length} 个文件");
        bool enabled = count > 0 || AmendCheck.IsChecked == true;
        CommitSelectedButton.IsEnabled = CommitPushButton.IsEnabled = enabled;
    }
    private void SelectAllChangesClick(object sender, RoutedEventArgs e)
    {
        _bulkSelecting = true;
        try { foreach (var change in (_gitStatus?.Changes ?? []).Where(c => !c.IsConflict)) _commitChoices[change.Path].Included = SelectAllChanges.IsChecked == true; }
        finally { _bulkSelecting = false; }
        UpdateCommitSelection();
    }
    private void AmendChanged(object sender, RoutedEventArgs e) => UpdateCommitSelection();
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
    private void ClosePreview()
    {
        _diffLoad?.Cancel();
        _preview = null;
        SetSurface(false); FocusEditor();
    }

    private async Task ShowPreviewAsync(DiffPreview preview)
    {
        var git = _git;
        if (git == null) return;
        ++_openRequest;
        _diffLoad?.Cancel(); _diffLoad?.Dispose(); _diffLoad = new();
        var token = _diffLoad.Token;
        var diff = preview.Revision != null && preview.File != null
            ? await git.RevisionDiffAsync(preview.Revision, preview.File, token)
            : await git.DiffAsync(preview.Change!, preview.Staged, token);
        token.ThrowIfCancellationRequested();
        if (diff.OldText.Contains('\0') || diff.NewText.Contains('\0')) throw new IOException("二进制文件无法显示文本差异。");
        if (diff.OldText.Length + diff.NewText.Length > 1_000_000 || diff.OldText.Count(c => c == '\n') + diff.NewText.Count(c => c == '\n') > 40_000)
            throw new IOException("差异超过视图上限（两侧共 100 万字符或 4 万行）。可在项目终端用 Git 查看。");
        bool ignore = IgnoreWhitespace.IsChecked == true;
        if (_preview?.Description == preview.Description && _renderedDiff == diff && _renderedIgnore == ignore)
        {
            _preview = preview;
            SetSurface(true);
            return;
        }
        var model = await Task.Run(() => new SideBySideDiffBuilder(new Differ()).BuildDiffModel(diff.OldText, diff.NewText, ignore), token);
        token.ThrowIfCancellationRequested();
        _preview = preview;
        _renderedDiff = diff; _renderedIgnore = ignore;
        _diffScroll.Reset();
        EditorSupport.SetDiffText(OldEditor, model.OldText.Lines, preview.File?.OldPath ?? preview.Path, true);
        EditorSupport.SetDiffText(NewEditor, model.NewText.Lines, preview.Path, false);
        UiText.Set(OldLabel, "Text", () => TranslateDiffLabel(diff.OldLabel));
        UiText.Set(NewLabel, "Text", () => TranslateDiffLabel(diff.NewLabel));
        _diffLines = model.NewText.Lines.Select((line, i) => (line, i)).Where(x => x.line.Type != ChangeType.Unchanged &&
            (x.i == 0 || model.NewText.Lines[x.i - 1].Type == ChangeType.Unchanged)).Select(x => x.i + 1).ToArray();
        _diffIndex = -1;
        UiText.Set(DiffSummary, "Text", _diffLines.Length == 0 ? "没有文本差异" : $"{_diffLines.Length} 处差异");
        SetSurface(true);
        SetStatus(preview.Description);
        MoveDiff(1);
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
        if (HistoryFiles.SelectedItem is TreeViewItem { Tag: RevisionFile }) await Guard(OpenHistoryFileAsync);
    }
    private async void HistoryFileDiffClick(object sender, RoutedEventArgs e) => await Guard(OpenHistoryFileAsync);
    private async void LogDoubleClick(object sender, MouseButtonEventArgs e) => await Guard(async () =>
    {
        if (_git != null && LogList.SelectedItem is LogRow row)
            ShowOutput("git show " + row.Revision.ShortHash, await _git.ShowCommitAsync(row.Revision.Hash));
    });
}

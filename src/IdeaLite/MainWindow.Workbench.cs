using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using IdeaLite.Core;

namespace IdeaLite;

public partial class MainWindow
{
    private readonly WorkbenchLayout _layout = WorkbenchLayout.Load();
    private LeftTool _left;
    private BottomTool _bottom;
    private bool _mavenOpen, _showDiff;
    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void InitializeWorkbench()
    {
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        _left = _layout.Left;
        _bottom = _layout.Bottom;
        _mavenOpen = _layout.Maven;
        if (_layout.BottomCommit && _left == LeftTool.Commit) _left = LeftTool.Project;
        if (!_layout.BottomCommit && _bottom == BottomTool.LocalChanges) _bottom = BottomTool.Log;
        MoveCommitPane();
        RunConfigButton.IsEnabled = RunButton.IsEnabled = false;
        ApplyLayout();
        SetSurface(false);
    }

    private void RememberSizes()
    {
        if (LeftHost.IsVisible && LeftColumn.ActualWidth > 60) _layout.LeftWidth = LeftColumn.ActualWidth;
        if (MavenPane.IsVisible && RightColumn.ActualWidth > 60) _layout.RightWidth = RightColumn.ActualWidth;
        if (BottomPane.IsVisible && BottomRow.ActualHeight > 60) _layout.BottomHeight = BottomRow.ActualHeight;
        _layout.Left = _left;
        _layout.Bottom = _bottom;
        _layout.Maven = _mavenOpen;
        _layout.Language = UiText.Current.Language;
    }

    private void ApplyLayout()
    {
        bool left = !_focusMode && _left != LeftTool.None;
        bool right = !_focusMode && _mavenOpen;
        bool bottom = !_focusMode && _bottom != BottomTool.None;
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : Height;
        LeftColumn.Width = new(left ? Math.Clamp(_layout.LeftWidth, 170, Math.Max(180, width * .36)) : 0);
        RightColumn.Width = new(right ? Math.Clamp(_layout.RightWidth, 160, Math.Max(170, width * .29)) : 0);
        BottomRow.Height = new(bottom ? Math.Clamp(_layout.BottomHeight, 145, Math.Max(150, height * .62)) : 0);
        LeftSplitColumn.Width = new(left ? 4 : 0);
        RightSplitColumn.Width = new(right ? 4 : 0);
        BottomSplitRow.Height = new(bottom ? 4 : 0);
        LeftHost.Visibility = Visible(left);
        ProjectPane.Visibility = Visible(_left == LeftTool.Project);
        LeftCommitHost.Visibility = Visible(_left == LeftTool.Commit && !_layout.BottomCommit);
        StructurePane.Visibility = Visible(_left == LeftTool.Structure);
        MavenPane.Visibility = Visible(right);
        BottomPane.Visibility = Visible(bottom);
        bool git = _bottom is BottomTool.Log or BottomTool.Console or BottomTool.LocalChanges;
        UiText.Set(BottomTitle, "Text", git ? "Git" : _bottom == BottomTool.Terminal ? "终端" : _bottom == BottomTool.Services ? "服务" : "构建");
        LogTab.Visibility = ConsoleTab.Visibility = Visible(git);
        LocalTab.Visibility = Visible(git && _layout.BottomCommit);
        LogPane.Visibility = Visible(_bottom == BottomTool.Log);
        OutputPanel.Visibility = Visible(_bottom is BottomTool.Console or BottomTool.Run);
        TerminalPane.Visibility = TerminalTabScroll.Visibility = AddTerminalButton.Visibility = Visible(_bottom == BottomTool.Terminal);
        OutputTitle.Visibility = OutputPanel.Visibility;
        SpringList.Visibility = Visible(_bottom == BottomTool.Services);
        BottomCommitHost.Visibility = Visible(_bottom == BottomTool.LocalChanges && _layout.BottomCommit);
        ProjectRail.Tag = left && _left == LeftTool.Project ? "active" : null;
        CommitRail.Tag = left && _left == LeftTool.Commit || bottom && _bottom == BottomTool.LocalChanges ? "active" : null;
        StructureRail.Tag = left && _left == LeftTool.Structure ? "active" : null;
        GitRail.Tag = bottom && git ? "active" : null;
        ServicesRail.Tag = bottom && _bottom == BottomTool.Services ? "active" : null;
        OutputRail.Tag = bottom && _bottom == BottomTool.Run ? "active" : null;
        TerminalRail.Tag = bottom && _bottom == BottomTool.Terminal ? "active" : null;
        MavenRail.Tag = right ? "active" : null;
        LogTab.Tag = _bottom == BottomTool.Log ? "active" : null;
        ConsoleTab.Tag = _bottom == BottomTool.Console ? "active" : null;
        LocalTab.Tag = _bottom == BottomTool.LocalChanges ? "active" : null;
    }

    private void SelectLeft(LeftTool tool, bool toggle = true)
    {
        RememberSizes();
        bool hide = toggle && !_focusMode && _left == tool;
        _left = hide ? LeftTool.None : tool;
        _focusMode = false;
        ApplyLayout();
        if (_left == LeftTool.Structure) { UpdateStructure(); StructureList.Focus(); }
        else if (_left == LeftTool.Project) FileTree.Focus();
        else if (_left == LeftTool.None) FocusEditor();
    }

    private async Task SelectBottomAsync(BottomTool tool, bool toggle = true)
    {
        RememberSizes();
        bool hide = toggle && !_focusMode && tool == _bottom;
        _bottom = hide ? BottomTool.None : tool;
        _focusMode = false;
        ApplyLayout();
        if (_bottom == BottomTool.Log)
        {
            await LoadHistoryAsync();
            LogList.Focus();
        }
        else if (_bottom == BottomTool.Services) SpringList.Focus();
        else if (_bottom == BottomTool.None) FocusEditor();
    }

    private async Task ActivateCommitAsync(bool toggle)
    {
        if (_layout.BottomCommit) await SelectBottomAsync(BottomTool.LocalChanges, toggle);
        else SelectLeft(LeftTool.Commit, toggle);
        if (!CommitPane.IsVisible) return;
        await RefreshGitAsync();
        if (toggle) ChangesList.Focus(); else CommitMessage.Focus();
    }
    private void MoveCommitPane()
    {
        var destination = _layout.BottomCommit ? BottomCommitHost : LeftCommitHost;
        if (ReferenceEquals(destination.Content, CommitPane)) return;
        if (ReferenceEquals(LeftCommitHost.Content, CommitPane)) LeftCommitHost.Content = null;
        else BottomCommitHost.Content = null;
        destination.Content = CommitPane;
    }
    private void ChangeCommitLocation()
    {
        RememberSizes();
        _layout.BottomCommit = !_layout.BottomCommit;
        MoveCommitPane();
        _focusMode = false;
        if (_layout.BottomCommit) { _left = LeftTool.Project; _bottom = BottomTool.LocalChanges; }
        else { _left = LeftTool.Commit; if (_bottom == BottomTool.LocalChanges) _bottom = BottomTool.Log; }
        ApplyLayout();
        RememberSizes(); _layout.Save();
    }

    private void SetSurface(bool diff)
    {
        _showDiff = diff;
        DiffPane.Visibility = Visible(diff);
        Editor.Visibility = Visible(!diff && _active != null);
        WelcomePane.Visibility = Visible(!diff && _active == null);
        string? path = diff ? _preview?.Path : _active == null ? null : Relative(_active.File.Path);
        Breadcrumb.Text = (_workspace == null ? "Idea Lite" : Path.GetFileName(_workspace.Root)) +
            (path == null ? "" : "   ›   " + path.Replace("/", "   ›   "));
        RebuildTabs();
    }
    private void FocusEditor()
    {
        if (_showDiff) NewEditor.TextArea.Focus();
        else if (_active != null) Editor.TextArea.Focus();
    }
    private async Task CycleTabAsync(bool reverse)
    {
        int count = _documents.Count + (_preview != null ? 1 : 0);
        if (count < 2) return;
        int current = _showDiff ? _documents.Count : _documents.IndexOf(_active!);
        int next = (current + (reverse ? -1 : 1) + count) % count;
        if (next == _documents.Count && _preview != null) await ShowPreviewAsync(_preview);
        else await OpenFileAsync(Relative(_documents[next].File.Path));
    }

    private void AddEditorTab(string name, string kind, string description, bool selected, bool dirty,
        Func<Task> open, Func<Task> close)
    {
        var tab = new Border
        {
            Background = (Brush)FindResource(selected ? "Panel" : "Bg"),
            BorderBrush = selected ? (Brush)FindResource("Accent") : Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 2),
            ToolTip = description
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new IdeIcon { Kind = kind, Width = 14, Height = 14, Foreground = IconBrush(kind) });
        title.Children.Add(new TextBlock { Text = name + (dirty ? " •" : ""), Margin = new Thickness(5, 0, 0, 0) });
        var openButton = new Button { Style = (Style)FindResource("FlatButton"), Content = title, Padding = new Thickness(6, 1, 2, 1) };
        openButton.Click += async (_, _) => await Guard(open);
        var closeButton = new Button { Content = new IdeIcon { Kind = "Close", Width = 11, Height = 11 }, Style = (Style)FindResource("FlatButton"), ToolTip = "关闭标签 · Ctrl+W", Padding = new Thickness(3, 1, 3, 1) }.Localize("ToolTip");
        closeButton.Click += async (_, _) => await Guard(close);
        row.Children.Add(openButton); row.Children.Add(closeButton); tab.Child = row;
        tab.MouseUp += async (_, e) => { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; await Guard(close); } };
        DocumentTabs.Children.Add(tab);
        if (selected) tab.BringIntoView();
    }

    private static Brush IconBrush(string kind) => kind switch
    {
        "Class" => Brushes.CornflowerBlue,
        "Project" or "Module" or "SourceRoot" or "Diff" or "Maven" => Brushes.CornflowerBlue,
        "Xml" => Brushes.BurlyWood,
        _ => Brushes.LightSlateGray
    };
    internal static StackPanel FileHeader(string name, string icon, string tooltip)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = tooltip };
        header.Children.Add(new IdeIcon { Kind = icon, Width = 15, Height = 15, Foreground = IconBrush(icon) });
        header.Children.Add(new TextBlock { Text = name, Margin = new Thickness(6, 0, 0, 0), FontSize = 13 });
        return header;
    }
    private void RenderProjectTree()
    {
        if (_workspace == null) return;
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var selectedPath = (FileTree.SelectedItem as TreeViewItem)?.Tag is FileNode selected ? selected.Path : null;
        void Visit(ItemsControl parent, bool restore)
        {
            foreach (var item in parent.Items.OfType<TreeViewItem>())
            {
                if (item.Tag is not FileNode node) continue;
                if (restore && node.Path == selectedPath) item.IsSelected = true;
                if (node.IsFile) continue;
                if (restore && expanded.Contains(node.Path)) item.IsExpanded = true;
                if (!item.IsExpanded) continue;
                if (!restore) expanded.Add(node.Path);
                Visit(item, restore);
            }
        }
        Visit(FileTree, false);
        FileTree.Items.Clear();
        var artifact = _workspace.Maven.FirstOrDefault(module => module.Path == "pom.xml")?.Artifact;
        var name = Path.GetFileName(_workspace.Root);
        var rootName = name + (string.IsNullOrWhiteSpace(artifact) || artifact == name ? "" : " [" + artifact + "]");
        var root = new TreeViewItem { Tag = new FileNode("", false), Header = FileHeader(rootName, "Project", _workspace.Root), IsExpanded = true, Style = (Style)FindResource("ProjectTreeItem") };
        FileTree.Items.Add(root);
        PopulateTree(root, "");
        Visit(FileTree, true);
        _layout.Recent.RemoveAll(p => string.Equals(p, _workspace.Root, StringComparison.OrdinalIgnoreCase));
        _layout.Recent.Insert(0, _workspace.Root);
        if (_layout.Recent.Count > 8) _layout.Recent.RemoveRange(8, _layout.Recent.Count - 8);
    }
    private void SelectCurrentFile()
    {
        SelectLeft(LeftTool.Project, false);
        var path = _showDiff ? _preview?.Path : _active != null ? Relative(_active.File.Path) : null;
        if (path == null || FileTree.Items.Count == 0) return;
        var item = (TreeViewItem)FileTree.Items[0];
        while (true)
        {
            item.IsExpanded = true;
            var next = item.Items.OfType<TreeViewItem>().FirstOrDefault(child => child.Tag is FileNode n &&
                (n.Path == path || path.StartsWith(n.Path + "/", StringComparison.Ordinal)));
            if (next == null) break;
            item = next;
            if (item.Tag is FileNode { IsFile: true })
            {
                item.IsSelected = true; item.BringIntoView(); item.Focus(); break;
            }
        }
    }
    private void UpdateStructure()
    {
        StructureList.ItemsSource = _active != null && _workspace != null
            ? _workspace.Symbols.Where(s => s.Path == Relative(_active.File.Path)).OrderBy(s => s.Line).ToArray() : null;
    }
    private async void StructureOpen(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt<ListBoxItem>(StructureList, e.OriginalSource) != null && StructureList.SelectedItem is SearchHit hit) await Guard(() => OpenFileAsync(hit.Path, hit.Line));
    }
    private static T? ItemAt<T>(ItemsControl parent, object origin) where T : DependencyObject =>
        origin is DependencyObject node ? ItemsControl.ContainerFromElement(parent, node) as T : null;
    private static TreeViewItem? TreeItemAt(object origin)
    {
        for (var node = origin as DependencyObject; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is TreeViewItem item) return item;
        return null;
    }
    private void TreeRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject node) return;
        while (node is not TreeViewItem && node is Visual) node = VisualTreeHelper.GetParent(node);
        if (node is TreeViewItem item) item.IsSelected = true;
    }
    private void ChangeRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt<ListBoxItem>(ChangesList, e.OriginalSource) is { IsSelected: false } item) ChangesList.SelectedItem = item.Content;
    }

    private static void MenuAction(ItemsControl parent, string header, Func<Task> action, string shortcut = "")
    {
        var item = new MenuItem { Header = header, InputGestureText = shortcut };
        if (!Path.IsPathFullyQualified(header)) item.Localize("Header");
        item.Click += async (_, _) => await ((MainWindow)Application.Current.MainWindow).Guard(action);
        parent.Items.Add(item);
    }
    private static ContextMenu PopupMenu(UIElement target) => new() { PlacementTarget = target, Placement = PlacementMode.Bottom };
    private void ProjectMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = PopupMenu((UIElement)sender);
        MenuAction(menu, "打开项目…", ChooseProjectAsync, "Ctrl+O");
        if (_layout.Recent.Count > 0) menu.Items.Add(new Separator());
        foreach (var path in _layout.Recent.Where(Directory.Exists))
            MenuAction(menu, path, () => OpenProjectInstanceAsync(path));
        menu.IsOpen = true;
    }
    private void MainMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = PopupMenu((UIElement)sender);
        MenuItem Group(string title) { var item = new MenuItem { Header = title }.Localize("Header"); menu.Items.Add(item); return item; }
        var file = Group("文件");
        MenuAction(file, "打开项目…", ChooseProjectAsync, "Ctrl+O");
        MenuAction(file, "保存", () => SaveAsync(), "Ctrl+S");
        MenuAction(file, "从磁盘重新加载全部文件", ReloadAllFromDiskAsync, "Ctrl+Alt+Y");
        var view = Group("视图");
        MenuAction(view, "项目", () => { SelectLeft(LeftTool.Project, false); return Task.CompletedTask; }, "Alt+1");
        MenuAction(view, "提交", () => ActivateCommitAsync(false), "Alt+0");
        MenuAction(view, "Git", () => SelectBottomAsync(BottomTool.Log, false), "Alt+9");
        MenuAction(view, "服务", () => SelectBottomAsync(BottomTool.Services, false), "Alt+8");
        MenuAction(view, "Maven", () => { ToggleMaven(); return Task.CompletedTask; });
        MenuAction(view, "隐藏或恢复工具窗口", () => { ToggleFocusMode(); return Task.CompletedTask; }, "Ctrl+Shift+F12");
        var navigate = Group("导航");
        MenuAction(navigate, "随处搜索…", () => SearchAsync(), "Shift × 2");
        MenuAction(navigate, "文件…", () => SearchAsync("files"), "Ctrl+Shift+N");
        MenuAction(navigate, "返回", GoBackAsync, "Ctrl+Alt+Left");
        var git = Group("Git");
        MenuAction(git, "提交…", () => ActivateCommitAsync(false), "Ctrl+K");
        MenuAction(git, "推送…", PushWithPreviewAsync, "Ctrl+Shift+K");
        MenuAction(git, "拉取", () => RunGitCommandAsync(["pull", "--ff-only"]));
        MenuAction(git, "分支…", ShowBranchesAsync);
        MenuAction(git, "更多 Git 操作…", () => { GitMenuClick(BranchButton, new RoutedEventArgs()); return Task.CompletedTask; });
        MenuAction(menu, "快捷键", () => { HelpClick(this, new RoutedEventArgs()); return Task.CompletedTask; });
        menu.IsOpen = true;
    }
    private void ShowSettingsMenu()
    {
        var menu = PopupMenu(SettingsButton);
        menu.Items.Add(LanguageMenu());
        MenuAction(menu, "AI 提交设置…", () => { ShowAiCommitSettings(); return Task.CompletedTask; });
        MenuAction(menu, "构建工具与环境…", () => { BuildSettingsClick(); return Task.CompletedTask; });
        MenuAction(menu, _layout.BottomCommit ? "提交窗口位置：移到左侧" : "提交窗口位置：移到底部 Git", () => { ChangeCommitLocation(); return Task.CompletedTask; });
        MenuAction(menu, "隐藏或恢复工具窗口", () => { ToggleFocusMode(); return Task.CompletedTask; }, "Ctrl+Shift+F12");
        MenuAction(menu, "快捷键", () => { HelpClick(this, new RoutedEventArgs()); return Task.CompletedTask; });
        menu.IsOpen = true;
    }

    private MenuItem LanguageMenu()
    {
        var menu = new MenuItem { Header = "语言" }.Localize("Header");
        var chineseName = "简体中文";
        foreach (var choice in new[] { (Code: "en", Name: "English"), (Code: "zh-CN", Name: chineseName) })
        {
            var item = new MenuItem { Header = choice.Name, IsCheckable = true, IsChecked = UiText.Current.Language == choice.Code };
            item.Click += (_, _) => ChangeLanguage(choice.Code);
            menu.Items.Add(item);
        }
        return menu;
    }
    private void LanguageClick(object sender, RoutedEventArgs e)
    {
        var menu = PopupMenu(LanguageButton);
        var languages = LanguageMenu();
        while (languages.Items.Count > 0)
        {
            var item = languages.Items[0]; languages.Items.RemoveAt(0);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private void ChangeLanguage(string language)
    {
        _layout.Language = language; _layout.Save();
        UiText.Current.Language = language;
    }
    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UiText.Language)) return;
        _layout.Language = UiText.Current.Language;
        if (LogRefs.Items.Count > 0) RenderRefs();
        UpdateTypeUsages();
        RebuildTabs();
        if (_outputMessage != null) OutputEditor.Text = UiText.Current.Translate(_outputMessage);
    }
    private void ChangeActionsClick(object sender, RoutedEventArgs e)
    {
        var menu = PopupMenu((UIElement)sender);
        MenuAction(menu, "显示差异", OpenSelectedChangeAsync, "Ctrl+D");
        MenuAction(menu, "打开源文件", OpenSelectedSourceAsync, "F4");
        MenuAction(menu, "全部暂存", () => { StageAllClick(this, new RoutedEventArgs()); return Task.CompletedTask; });
        MenuAction(menu, "回滚所选…", DiscardSelectionAsync, "Ctrl+Alt+Z");
        menu.IsOpen = true;
    }
    private void ToggleMaven()
    {
        RememberSizes(); _mavenOpen = _focusMode || !_mavenOpen; _focusMode = false; ApplyLayout();
    }
    private void HideFocusedTool()
    {
        if (BottomPane.IsKeyboardFocusWithin) _bottom = BottomTool.None;
        else if (MavenPane.IsKeyboardFocusWithin) _mavenOpen = false;
        else if (LeftHost.IsKeyboardFocusWithin) _left = LeftTool.None;
        ApplyLayout(); FocusEditor();
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeClick(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void CloseWindowClick(object sender, RoutedEventArgs e) => Close();
    private void ProjectToolClick(object sender, RoutedEventArgs e) => SelectLeft(LeftTool.Project);
    private void StructureToolClick(object sender, RoutedEventArgs e) => SelectLeft(LeftTool.Structure);
    private async void CommitToolClick(object sender, RoutedEventArgs e) => await Guard(() => ActivateCommitAsync(true));
    private async void GitToolClick(object sender, RoutedEventArgs e) => await Guard(() => SelectBottomAsync(BottomTool.Log));
    private async void ServicesToolClick(object sender, RoutedEventArgs e) => await Guard(() => SelectBottomAsync(BottomTool.Services));
    private async void RunToolClick(object sender, RoutedEventArgs e) => await Guard(() => SelectBottomAsync(BottomTool.Run));
    private async void LogTabClick(object sender, RoutedEventArgs e) => await Guard(() => SelectBottomAsync(BottomTool.Log, false));
    private async void LocalTabClick(object sender, RoutedEventArgs e) => await Guard(() => ActivateCommitAsync(false));
    private async void ConsoleTabClick(object sender, RoutedEventArgs e) => await Guard(() => SelectBottomAsync(BottomTool.Console, false));
    private void MavenToolClick(object sender, RoutedEventArgs e) => ToggleMaven();
    private void SettingsClick(object sender, RoutedEventArgs e) => ShowSettingsMenu();
    private void TerminalClick(object sender, RoutedEventArgs e) => OpenTerminal();
    private void SelectCurrentClick(object sender, RoutedEventArgs e) => SelectCurrentFile();
    private void CollapseTreeClick(object sender, RoutedEventArgs e)
    {
        static void Collapse(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<TreeViewItem>())
            {
                if (item.IsExpanded) Collapse(item);
                item.IsExpanded = false;
            }
        }
        if (FileTree.Items.Count > 0) Collapse((TreeViewItem)FileTree.Items[0]);
    }
    private void HideBottomClick(object sender, RoutedEventArgs e) { RememberSizes(); _bottom = BottomTool.None; ApplyLayout(); FocusEditor(); }
    private void HideLeftClick(object sender, RoutedEventArgs e) { RememberSizes(); _left = LeftTool.None; ApplyLayout(); FocusEditor(); }
    private void HideCommitClick(object sender, RoutedEventArgs e) { if (_layout.BottomCommit) HideBottomClick(sender, e); else HideLeftClick(sender, e); }
    private async void BottomRefreshClick(object sender, RoutedEventArgs e) => await Guard(_bottom == BottomTool.Log ? LoadHistoryAsync : RefreshWorkspaceAsync);
}

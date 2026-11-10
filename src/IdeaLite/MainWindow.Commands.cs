using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using IdeaLite.Core;

namespace IdeaLite;

public partial class MainWindow
{
    private sealed record BranchChoice(string Name, string Upstream, bool Current, string Group, GitRef Reference);
    private void GitMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = PopupMenu(sender as Button ?? BranchButton);
        Add("提交…", () => ActivateCommitAsync(false));
        Add("拉取（仅快进）", () => RunGitCommandAsync(["pull", "--ff-only"]));
        Add("推送…", PushWithPreviewAsync);
        Add("显示 Git 日志", () => SelectBottomAsync(BottomTool.Log, false));
        menu.Items.Add(new Separator());
        Add("刷新 / Fetch", async () => await RunGitCommandAsync(["fetch", "--all"]));
        Add("分支与标签…", ShowBranchesAsync);
        Add("创建分支…", async () =>
        {
            var name = Dialogs.Prompt(this, "创建并切换分支", "输入新分支名称");
            if (!string.IsNullOrWhiteSpace(name)) await SwitchBranchAsync(["-c", name]);
        });
        menu.Items.Add(new Separator());
        Add("Stash：保存工作区（含未跟踪文件）…", async () =>
        {
            var message = Dialogs.Prompt(this, "暂存工作现场", "stash 会收起工作区改动，包含未跟踪文件。请输入说明：", "Idea Lite stash");
            if (message != null && await CheckUnsavedBuffersAsync()) await RunGitCommandAsync(["stash", "push", "-u", "-m", message]);
        });
        Add("Stash：查看列表", async () => await RunGitCommandAsync(["stash", "list"]));
        Add("Stash：恢复最近一项（保留 stash）", async () => await RunGitCommandAsync(["stash", "apply", "--index", "stash@{0}"]));
        Add("Stash：按编号恢复…", async () =>
        {
            var hash = Dialogs.Prompt(this, "恢复 Stash", "输入 Stash 的哈希。");
            if (!string.IsNullOrWhiteSpace(hash)) await RunGitCommandAsync(["stash", "apply", "--index", "--", hash]);
        });
        Add("Stash：恢复并移除最近一项…", async () =>
        {
            if (Dialogs.Confirm(this, "恢复最近的 stash，成功后从 stash 列表移除。是否继续？")) await RunGitCommandAsync(["stash", "pop", "--index"]);
        });
        menu.Items.Add(new Separator());
        Add("合并分支…", async () => await PromptGitAsync("合并分支", "merge", "输入要合并的分支。发生冲突后可使用差异面板处理。"));
        Add("Rebase…", async () => await PromptGitAsync("Rebase", "rebase", "输入目标分支。此操作重写当前分支提交。"));
        Add("Cherry-pick…", async () => await PromptGitAsync("Cherry-pick", "cherry-pick", "输入一个提交 ID"));
        Add("继续 Cherry-pick", () => RunGitCommandAsync(["cherry-pick", "--continue"]));
        Add("中止 Cherry-pick", () => RunGitCommandAsync(["cherry-pick", "--abort"]));
        Add("Revert…", async () => await PromptGitAsync("创建反向提交", "revert", "输入要撤销的提交 ID（保留历史）"));
        Add("继续合并 / Rebase…", async () => await AdvancedGitAsync("merge --continue"));
        Add("中止合并 / Rebase…", async () => await AdvancedGitAsync("merge --abort"));
        menu.Items.Add(new Separator());
        Add("查看远程仓库", async () => await RunGitCommandAsync(["remote", "-v"]));
        Add("完整 Git 命令…", async () => await AdvancedGitAsync());
        Add("在 Git Bash 中打开项目", () => { OpenTerminal(); return Task.CompletedTask; });
        Add("初始化当前项目 Git 仓库…", async () =>
        {
            if (_workspace == null || _git != null) return;
            if (!Dialogs.Confirm(this, "在当前目录初始化 Git 仓库？\n" + _workspace.Root)) return;
            var service = new GitService(_workspace.Root);
            (await service.RunAsync(["init", "-b", "main"])).EnsureSuccess();
            _git = service; await RefreshWorkspaceAsync();
        });
        menu.IsOpen = true;
        void Add(string header, Func<Task> action)
        {
            var item = new MenuItem { Header = header }.Localize("Header");
            item.Click += async (_, _) => await Guard(action); menu.Items.Add(item);
        }
    }

    private async void BranchClick(object sender, RoutedEventArgs e) => await Guard(ShowBranchesAsync);

    private async Task ShowBranchesAsync()
    {
        var git = _git;
        if (git == null)
        {
            GitMenuClick(BranchButton, new RoutedEventArgs());
            return;
        }
        var refsTask = git.ReferencesAsync(); var recentTask = git.RecentBranchesAsync();
        await Task.WhenAll(refsTask, recentTask);
        var refs = await refsTask; var recent = await recentTask;
        if (git != _git) return;
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = BranchButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            VerticalOffset = 3
        };
        var panel = new DockPanel { Margin = new Thickness(8) };
        var search = new TextBox { ToolTip = "搜索分支或标签", Margin = new Thickness(3, 3, 3, 8), Style = (Style)FindResource("SearchField") }.Localize("ToolTip");
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索分支或标签");
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        var actions = new StackPanel();
        void Action(string text, string icon, string shortcut, Func<Task> run)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new IdeIcon { Kind = icon, Width = 15, Height = 15 });
            var title = new TextBlock { Text = text }.Localize("Text"); Grid.SetColumn(title, 1); row.Children.Add(title);
            var key = new TextBlock { Text = shortcut, Foreground = (System.Windows.Media.Brush)FindResource("Muted"), FontSize = 11 }; Grid.SetColumn(key, 2); row.Children.Add(key);
            var button = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Style = (Style)FindResource("FlatButton"), Padding = new Thickness(8, 5, 8, 5) };
            button.Click += async (_, _) => { popup.IsOpen = false; await Guard(run); };
            actions.Children.Add(button);
        }
        Action("更新项目…", "Pull", "Ctrl+T", () => RunGitCommandAsync(["pull", "--ff-only"]));
        Action("提交…", "Commit", "Ctrl+K", () => ActivateCommitAsync(false));
        Action("推送…", "Push", "Ctrl+Shift+K", PushWithPreviewAsync);
        actions.Children.Add(new Separator());
        Action("新建分支…", "Plus", "Ctrl+Alt+N", CreateBranchAsync);
        Action("签出标签或提交…", "Tag", "", async () =>
        {
            var revision = Dialogs.Prompt(this, "签出标签或提交", "输入标签或提交 ID（分离 HEAD）");
            if (!string.IsNullOrWhiteSpace(revision)) await SwitchBranchAsync(["--detach", "--", revision]);
        });
        actions.Children.Add(new Separator());
        DockPanel.SetDock(actions, Dock.Top); panel.Children.Add(actions);
        var list = new ListBox { ItemTemplate = (DataTemplate)FindResource("BranchRow"), ItemContainerStyle = (Style)FindResource("CompactRow") };
        list.GroupStyle.Add(new GroupStyle { ContainerStyle = (Style)FindResource("BranchGroupItem") });
        panel.Children.Add(list);
        var border = new Border
        {
            Width = 390,
            Height = 490,
            Background = (System.Windows.Media.Brush)FindResource("Panel"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = panel
        };
        var branchActions = new StackPanel { Margin = new Thickness(7) };
        var actionPane = new Border
        {
            Width = 254, Height = border.Height, Child = new ScrollViewer { Content = branchActions },
            Background = border.Background, BorderBrush = border.BorderBrush, BorderThickness = border.BorderThickness,
            CornerRadius = border.CornerRadius, Visibility = Visibility.Collapsed
        };
        var popupContent = new StackPanel { Orientation = Orientation.Horizontal };
        popupContent.Children.Add(border);
        popupContent.Children.Add(actionPane);
        popup.Child = popupContent;
        void Filter()
        {
            actionPane.Visibility = Visibility.Collapsed;
            var choices = recent.Select(name => refs.FirstOrDefault(reference => reference.Group == "本地" && reference.Name == name)).OfType<GitRef>()
                .Select(reference => new BranchChoice(reference.Name, reference.Upstream, reference.Current, "最近", reference))
                .Concat(refs.OrderBy(reference => reference.Group == "本地" ? 0 : reference.Group == "远程" ? 1 : 2).ThenByDescending(reference => reference.Current)
                    .Select(reference => new BranchChoice(reference.Name, reference.Upstream, reference.Current, reference.Group, reference)))
                .Where(choice => choice.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).Take(500).ToList();
            var view = new System.Windows.Data.ListCollectionView(choices);
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(BranchChoice.Group)));
            list.ItemsSource = view;
            list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
        }
        async Task Checkout()
        {
            if (list.SelectedItem is not BranchChoice choice) return;
            var selected = choice.Reference;
            popup.IsOpen = false;
            if (selected.Current || git != _git) return;
            await Guard(() => SwitchBranchAsync(CheckoutArguments(selected, refs)));
        }
        void ShowActions(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ItemAt<ListBoxItem>(list, e.OriginalSource) is not { Content: BranchChoice choice } row) return;
            row.IsSelected = true;
            branchActions.Children.Clear();
            branchActions.Children.Add(new TextBlock
            {
                Text = choice.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 5, 6, 7), TextTrimming = TextTrimming.CharacterEllipsis
            });
            foreach (var (title, run) in BranchActions(choice.Reference, refs))
            {
                var button = new Button
                {
                    Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap }.Localize("Text"),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                    Style = (Style)FindResource("FlatButton"), Padding = new Thickness(8, 5, 8, 5)
                };
                button.Click += async (_, _) => { popup.IsOpen = false; if (git == _git) await Guard(run); };
                branchActions.Children.Add(button);
            }
            actionPane.Visibility = Visibility.Visible;
            e.Handled = true;
        }
        search.TextChanged += (_, _) => Filter();
        panel.PreviewKeyDown += async (_, e) =>
        {
            Func<Task>? action = (System.Windows.Input.Keyboard.Modifiers, e.Key) switch
            {
                (System.Windows.Input.ModifierKeys.Control, System.Windows.Input.Key.T) => () => RunGitCommandAsync(["pull", "--ff-only"]),
                (System.Windows.Input.ModifierKeys.Control, System.Windows.Input.Key.K) => () => ActivateCommitAsync(false),
                (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift, System.Windows.Input.Key.K) => PushWithPreviewAsync,
                (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt, System.Windows.Input.Key.N) => CreateBranchAsync,
                _ => null
            };
            if (action != null) { e.Handled = true; popup.IsOpen = false; await Guard(action); return; }
            if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; popup.IsOpen = false; }
            else if (e.Key == System.Windows.Input.Key.Enter && (list.IsKeyboardFocusWithin || search.IsKeyboardFocusWithin)) { e.Handled = true; await Checkout(); }
            else if (e.Key == System.Windows.Input.Key.Down && search.IsKeyboardFocusWithin) { e.Handled = true; list.Focus(); }
        };
        list.PreviewMouseLeftButtonUp += ShowActions;
        list.PreviewMouseRightButtonUp += ShowActions;
        Filter();
        popup.Opened += (_, _) => search.Focus();
        popup.IsOpen = true;
    }

    private async Task CreateBranchAsync()
    {
        if (_git == null || _commandBusy) return;
        var name = Dialogs.Prompt(this, "新建分支", "分支名称");
        if (!string.IsNullOrWhiteSpace(name)) await SwitchBranchAsync(["-c", name]);
    }

    private Task<bool> SwitchBranchAsync(string[] arguments) =>
        RunGitCommandAsync(["switch", "--no-overwrite-ignore", .. arguments]);

    private static string[] CheckoutArguments(GitRef reference, IReadOnlyList<GitRef> references)
    {
        if (reference.Group == "标签") return ["--detach", "--", reference.FullName];
        if (reference.Group == "本地") return ["--no-guess", "--", reference.Name];
        var tracking = references.FirstOrDefault(candidate => candidate.Group == "本地" && candidate.Upstream == reference.Name);
        return tracking == null ? ["--track", "--", reference.Name] : ["--no-guess", "--", tracking.Name];
    }

    private IEnumerable<(string Title, Func<Task> Run)> BranchActions(GitRef reference, IReadOnlyList<GitRef> references)
    {
        var checkout = CheckoutArguments(reference, references);
        if (!reference.Current)
        {
            yield return ("切换到此分支或标签", () => SwitchBranchAsync(checkout));
            if (_gitStatus?.Changes.Count > 0 || _documents.Any(doc => doc.Dirty))
                yield return ("Stash 后切换…", () => StashAndSwitchAsync(checkout));
        }
        yield return ("从这里新建分支…", async () =>
        {
            var name = Dialogs.Prompt(this, "新建分支", "分支名称");
            if (!string.IsNullOrWhiteSpace(name)) await SwitchBranchAsync(["-c", name, "--", reference.FullName]);
        });
        if (reference.Group == "本地" && reference.Upstream.Length > 0)
            yield return (reference.Current ? "更新此分支（仅快进）" : "切换并更新（仅快进）", async () =>
            {
                if (await SwitchBranchAsync(checkout)) await RunGitCommandAsync(["pull", "--ff-only"]);
            });
        if (!reference.Current)
        {
            yield return ("比较与当前分支的提交", () => RunGitCommandAsync(["log", "--left-right", "--oneline", "HEAD..." + reference.FullName, "--"]));
            if (reference.Group != "标签")
            {
                yield return ("合并到当前分支", () => RunGitCommandAsync(["merge", "--no-edit", "--", reference.FullName]));
                yield return ("将当前分支 Rebase 到此分支…", () => RunGitCommandAsync(["rebase", "--", reference.FullName]));
            }
        }
        yield return ("比较与工作区的差异", () => RunGitCommandAsync(["diff", "--no-ext-diff", "--no-textconv", reference.FullName, "--"]));
        yield return ("从这里新建工作树…", async () =>
        {
            var name = Dialogs.Prompt(this, "新建工作树", "工作树使用的新分支名称");
            if (string.IsNullOrWhiteSpace(name)) return;
            var path = Dialogs.Prompt(this, "新建工作树", "输入新工作树的绝对目录路径。");
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!Path.IsPathFullyQualified(path)) { SetStatus("请输入绝对目录路径。"); return; }
            await RunGitCommandAsync(["worktree", "add", "-b", name, "--", path, reference.FullName]);
        });
        if (reference.Group == "本地")
        {
            yield return ("推送此分支…", () => PushBranchWithPreviewAsync(reference.Name));
            yield return ("重命名此分支…", async () =>
            {
                var name = Dialogs.Prompt(this, "重命名分支", "输入新分支名称", reference.Name);
                if (!string.IsNullOrWhiteSpace(name) && name != reference.Name)
                    await RunGitCommandAsync(["branch", "--move", "--", reference.Name, name]);
            });
            if (!reference.Current)
                yield return ("删除已合并分支…", () => RunGitCommandAsync(["branch", "--delete", "--", reference.Name]));
        }
    }

    private async Task StashAndSwitchAsync(string[] checkout)
    {
        var git = _git;
        if (git == null || _commandBusy || !Dialogs.Confirm(this, "将暂存、未暂存和未跟踪的改动保存到 Stash 后切换？可稍后恢复原改动。")) return;
        var head = (await git.RunAsync(["rev-parse", "--abbrev-ref", "HEAD"])).EnsureSuccess().Output.Trim();
        var label = $"Idea Lite checkout from {head} ({Guid.NewGuid():N})";
        if (git != _git || !await RunGitCommandAsync(["stash", "push", "--include-untracked", "-m", label])) return;
        var stashes = (await git.RunAsync(["stash", "list", "-n", "30", "--format=%h%x00%gs"])).EnsureSuccess().Output;
        var stash = stashes.Split('\n').FirstOrDefault(line => line.TrimEnd('\r').EndsWith(": " + label, StringComparison.Ordinal))?.Split('\0')[0];
        if (git != _git) return;
        try { await SwitchBranchAsync(checkout); }
        finally
        {
            if (git == _git && stash != null) SetStatus($"原改动已保存在 Stash {stash}，可在 Git 菜单中恢复。");
        }
    }

    private Task PushWithPreviewAsync() => PushBranchWithPreviewAsync(null);

    private async Task PushBranchWithPreviewAsync(string? branch)
    {
        var git = _git;
        if (git == null || _commandBusy) return;
        if (branch == null)
        {
            var head = await git.RunAsync(["symbolic-ref", "--short", "-q", "HEAD"]);
            if (head.ExitCode != 0) { SetStatus("请先切换到一个本地分支。"); return; }
            branch = head.Output.Trim();
        }
        var remotes = (await git.RunAsync(["remote"])).EnsureSuccess().Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (remotes.Length == 0) { SetStatus("没有远程仓库，可在 Git → 完整 Git 命令中配置 remote。"); return; }
        var upstream = (await git.RunAsync(["for-each-ref", "--format=%(upstream:remotename)%00%(upstream:remoteref)%00%(upstream:short)", "refs/heads/" + branch])).EnsureSuccess().Output.TrimEnd('\r', '\n').Split('\0');
        bool tracked = upstream.Length == 3 && remotes.Contains(upstream[0]) && upstream[1].StartsWith("refs/heads/", StringComparison.Ordinal);
        var remote = tracked ? upstream[0] : remotes.Contains("origin") ? "origin" : remotes[0];
        if (!tracked && remotes.Length > 1)
        {
            var choice = Dialogs.Prompt(this, "推送到远程仓库", "选择远程名称：" + string.Join("、", remotes), remote);
            if (choice == null) return;
            if (!remotes.Contains(choice)) throw new ArgumentException("请选择已配置的远程名称。");
            remote = choice;
        }
        var target = tracked ? upstream[1] : "refs/heads/" + branch;
        var range = tracked ? upstream[2] + "..refs/heads/" + branch : "refs/heads/" + branch;
        var commits = await git.RunAsync(["log", "-30", "--format=%h  %s", range, "--"]);
        if (commits.ExitCode != 0) throw new InvalidOperationException(commits.Combined.Trim());
        if (git != _git || !Dialogs.PushPreview(this, branch + "  →  " + remote + "/" + target["refs/heads/".Length..], commits.Output)) return;
        var refspec = "refs/heads/" + branch + ":" + target;
        await RunGitCommandAsync(tracked ? ["push", "--", remote, refspec] : ["push", "-u", "--", remote, refspec]);
    }

    private async Task<bool> CheckUnsavedBuffersAsync()
    {
        if (!_documents.Any(d => d.Dirty)) return true;
        if (!Dialogs.Confirm(this, "存在尚未保存的编辑。先保存全部文档后继续？\n选择“否”会取消 Git 操作。")) return false;
        var current = _active;
        foreach (var doc in _documents.Where(d => d.Dirty).ToArray())
        {
            await OpenFileAsync(Relative(doc.File.Path)); await SaveAsync();
            if (doc.Dirty) return false;
        }
        if (current != null) await OpenFileAsync(Relative(current.File.Path));
        return true;
    }

    private async Task PromptGitAsync(string title, string command, string description)
    {
        var value = Dialogs.Prompt(this, title, description);
        if (!string.IsNullOrWhiteSpace(value)) await RunGitCommandAsync([command, value]);
    }

    private async Task AdvancedGitAsync(string initial = "")
    {
        if (_git == null) return;
        var value = Dialogs.Prompt(this, "完整 Git 命令", "直接输入 git 后面的参数，支持引号，不经过 shell。\n例如：rebase --continue / remote add origin URL / push -u origin main / tag release-name / reflog / blame -- src/App.java", initial);
        if (string.IsNullOrWhiteSpace(value)) return;
        var arguments = ProcessRunner.SplitArguments(value);
        if (arguments.FirstOrDefault() == "git") arguments = arguments[1..];
        if (arguments.Length > 0) await RunGitCommandAsync(arguments);
    }

    private async Task<bool> RunGitCommandAsync(string[] arguments)
    {
        var git = _git;
        if (git == null || _commandBusy || arguments.Length == 0) return false;
        var command = arguments[0];
        var touchesWorkspace = command is not ("log" or "show" or "status" or "diff" or "blame" or "reflog" or "ls-files" or "ls-tree"
            or "cat-file" or "rev-parse" or "remote" or "push" or "fetch" or "branch") && !(command == "worktree" && arguments.ElementAtOrDefault(1) == "add");
        var destructive = command is "reset" or "clean" or "rebase" ||
            arguments.Any(a => a is "--hard" or "--force" or "-f" or "--force-with-lease" or "-D") ||
            (command == "stash" && arguments.Any(a => a is "drop" or "clear")) ||
            (command is "restore" or "checkout" && (arguments.Contains("--") || command == "restore")) ||
            arguments.Any(a => a is "--delete" or "--abort");
        _commandBusy = true;
        var label = "git " + string.Join(' ', arguments);
        try
        {
            if (touchesWorkspace && !await CheckUnsavedBuffersAsync()) return false;
            if (git != _git || destructive && !Dialogs.Confirm(this, "以下命令可能丢弃改动、删除引用或重写历史。请确认参数：\n\ngit " + string.Join(' ', arguments))) return false;
            _command?.Dispose(); _command = new();
            ShowOutput(label, "运行中…\n使用系统 Git 与现有凭据。可点击停止取消。", true);
            SetStatus(label + "…");
            var result = await git.RunAsync(arguments, _command.Token);
            ShowOutput(label, result.Combined.Length == 0 ? $"完成（退出码 {result.ExitCode}）" : result.Combined, result.Combined.Length == 0);
            if (touchesWorkspace && (result.ExitCode == 0 || command != "switch")) await RefreshWorkspaceAsync();
            else
            {
                await RefreshGitStatusAsync(refreshPreview: false);
                if (_bottom == BottomTool.Log) await LoadHistoryAsync();
            }
            SetStatus(label + (result.ExitCode == 0 ? " · 完成" : $" · 退出码 {result.ExitCode}，请查看输出"));
            if (result.ExitCode != 0 && command == "push") SetStatus("推送失败。已创建的提交仍可在本地分支的 Git Log 中查看。");
            else if (result.ExitCode != 0 && command == "switch" && result.Combined.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase))
                SetStatus("本地改动与目标分支冲突，切换已中止。可以先 Stash，再切换。");
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException) { ShowOutput(label, "命令已停止。请刷新并检查仓库状态。", true); return false; }
        finally { _commandBusy = false; CancelBuildButton.IsEnabled = false; }
    }

    private void OpenTerminal()
    {
        if (_workspace == null) return;
        RememberSizes(); _bottom = BottomTool.Terminal; _focusMode = false; ApplyLayout();
        if (_terminals.Count == 0) AddTerminal();
        else _activeTerminal?.View.Focus();
    }

    private async void MavenClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_workspace == null || _commandBusy) return;
        var selection = (MavenList.SelectedItem as TreeViewItem)?.Tag;
        var module = selection is MavenTask task ? task.Module : selection as MavenModule ?? _workspace.Maven.FirstOrDefault();
        if (module == null) { SetStatus("未检测到 pom.xml。"); return; }
        var value = Dialogs.Prompt(this, "运行 Maven 任务", "Maven 参数，例如：test / -DskipTests package / spring-boot:run\n优先使用项目 mvnw.cmd，其次使用 PATH 中的 mvn.cmd。", selection is MavenTask selected ? selected.Goal : "test");
        if (string.IsNullOrWhiteSpace(value)) return;
        await RunMavenAsync(module, value);
    });
}

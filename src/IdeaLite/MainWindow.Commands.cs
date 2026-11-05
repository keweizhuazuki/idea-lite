using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using IdeaLite.Core;

namespace IdeaLite;

public partial class MainWindow
{
    private void GitMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        Add("刷新 / Fetch", async () => await RunGitCommandAsync(["fetch", "--all"]));
        Add("分支与标签…", ShowBranchesAsync);
        Add("创建分支…", async () =>
        {
            var name = Dialogs.Prompt(this, "创建并切换分支", "输入新分支名称");
            if (!string.IsNullOrWhiteSpace(name)) await RunGitCommandAsync(["switch", "-c", name]);
        });
        menu.Items.Add(new Separator());
        Add("Stash：保存工作区（含未跟踪文件）…", async () =>
        {
            var message = Dialogs.Prompt(this, "暂存工作现场", "stash 会收起工作区改动，包含未跟踪文件。请输入说明：", "Idea Lite stash");
            if (message != null && await CheckUnsavedBuffersAsync()) await RunGitCommandAsync(["stash", "push", "-u", "-m", message]);
        });
        Add("Stash：查看列表", async () => await RunGitCommandAsync(["stash", "list"]));
        Add("Stash：恢复最近一项（保留 stash）", async () => await RunGitCommandAsync(["stash", "apply", "stash@{0}"]));
        Add("Stash：恢复并移除最近一项…", async () =>
        {
            if (Dialogs.Confirm(this, "恢复最近的 stash，成功后从 stash 列表移除。是否继续？")) await RunGitCommandAsync(["stash", "pop"]);
        });
        menu.Items.Add(new Separator());
        Add("合并分支…", async () => await PromptGitAsync("合并分支", "merge", "输入要合并的分支。发生冲突后可使用差异面板处理。"));
        Add("Rebase…", async () => await PromptGitAsync("Rebase", "rebase", "输入目标分支。此操作重写当前分支提交。"));
        Add("Cherry-pick…", async () => await PromptGitAsync("Cherry-pick", "cherry-pick", "输入一个提交 ID"));
        Add("Revert…", async () => await PromptGitAsync("创建反向提交", "revert", "输入要撤销的提交 ID（保留历史）"));
        Add("继续合并 / Rebase…", async () => await AdvancedGitAsync("merge --continue"));
        Add("中止合并 / Rebase…", async () => await AdvancedGitAsync("merge --abort"));
        menu.Items.Add(new Separator());
        Add("查看远程仓库", async () => await RunGitCommandAsync(["remote", "-v"]));
        Add("完整 Git 命令…", async () => await AdvancedGitAsync());
        Add("在终端中打开项目", () => { OpenTerminal(); return Task.CompletedTask; });
        Add("初始化当前项目 Git 仓库…", async () =>
        {
            if (_workspace == null || _git != null) return;
            if (!Dialogs.Confirm(this, "在当前目录初始化 Git 仓库？\n" + _workspace.Root)) return;
            var service = new GitService(_workspace.Root);
            (await service.RunAsync(["init", "-b", "main"])).EnsureSuccess();
            _git = service; await RefreshWorkspaceAsync();
        });
        menu.PlacementTarget = sender as Button; menu.IsOpen = true;
        void Add(string header, Func<Task> action)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await Guard(action); menu.Items.Add(item);
        }
    }

    private async void BranchClick(object sender, RoutedEventArgs e) => await Guard(ShowBranchesAsync);

    private async Task ShowBranchesAsync()
    {
        if (_git == null) return;
        var branches = (await _git.RunAsync(["branch", "--all", "--no-color"])).EnsureSuccess().Output;
        var tags = (await _git.RunAsync(["tag", "--list"])).EnsureSuccess().Output;
        ShowOutput("分支 / 标签", branches + "\n标签：\n" + tags);
        var value = Dialogs.Prompt(this, "切换分支", "输入本地分支名；输入新名称可用 Git 菜单创建分支。完整列表见输出面板。");
        if (!string.IsNullOrWhiteSpace(value) && await CheckUnsavedBuffersAsync()) await RunGitCommandAsync(["switch", value]);
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
        var value = Dialogs.Prompt(this, "完整 Git 命令", "直接输入 git 后面的参数，支持引号，不经过 shell。\n例如：rebase --continue / remote add origin URL / push -u origin main / tag v0.1.0 / reflog / blame -- src/App.java", initial);
        if (string.IsNullOrWhiteSpace(value)) return;
        var arguments = ProcessRunner.SplitArguments(value);
        if (arguments.FirstOrDefault() == "git") arguments = arguments[1..];
        if (arguments.Length > 0) await RunGitCommandAsync(arguments);
    }

    private async Task RunGitCommandAsync(string[] arguments)
    {
        if (_git == null || _commandBusy || arguments.Length == 0) return;
        var command = arguments[0];
        var mutates = command is not ("log" or "show" or "status" or "diff" or "blame" or "reflog" or "ls-files" or "ls-tree" or "cat-file" or "rev-parse" or "remote");
        if (mutates && !await CheckUnsavedBuffersAsync()) return;
        var destructive = command is "reset" or "clean" or "rebase" ||
            arguments.Any(a => a is "--hard" or "--force" or "-f" or "--force-with-lease" or "-D") ||
            (command == "stash" && arguments.Any(a => a is "drop" or "clear")) ||
            (command is "restore" or "checkout" && (arguments.Contains("--") || command == "restore")) ||
            arguments.Any(a => a is "--delete" or "--abort");
        if (destructive && !Dialogs.Confirm(this, "以下命令可能丢弃改动、删除引用或重写历史。请确认参数：\n\ngit " + string.Join(' ', arguments))) return;
        _commandBusy = true; _command?.Dispose(); _command = new();
        var label = "git " + string.Join(' ', arguments);
        ShowOutput(label, "运行中…\n使用系统 Git 与现有凭据。可点击停止取消。");
        SetStatus(label + "…");
        try
        {
            var result = await _git.RunAsync(arguments, _command.Token);
            ShowOutput(label, result.Combined.Length == 0 ? $"完成（退出码 {result.ExitCode}）" : result.Combined);
            await RefreshWorkspaceAsync();
            SetStatus(label + (result.ExitCode == 0 ? " · 完成" : $" · 退出码 {result.ExitCode}，请查看输出"));
            if (result.ExitCode != 0 && result.Combined.Contains("Authentication", StringComparison.OrdinalIgnoreCase)) SetStatus("Git 认证失败，请在项目终端中登录后重试。应用不保存密码。");
        }
        catch (OperationCanceledException) { ShowOutput(label, "命令已停止。请刷新并检查仓库状态。"); }
        finally { _commandBusy = false; }
    }

    private void OpenTerminal()
    {
        if (_workspace == null) return;
        var script = "Set-Location -LiteralPath '" + _workspace.Root.Replace("'", "''") + "'";
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = _workspace.Root };
        info.ArgumentList.Add("-NoLogo"); info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NoExit"); info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        Process.Start(info);
    }

    private async void MavenClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_workspace == null || _commandBusy) return;
        var module = MavenList.SelectedItem as MavenModule ?? _workspace.Maven.FirstOrDefault();
        if (module == null) { SetStatus("未检测到 pom.xml。"); return; }
        var value = Dialogs.Prompt(this, "运行 Maven 任务", "任务会启动项目构建、执行插件或下载依赖。请确认你信任此项目。\n例如：test / -DskipTests package / spring-boot:run\n优先使用项目 mvnw.cmd，其次使用 PATH 中的 mvn.cmd。", "test");
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!await CheckUnsavedBuffersAsync()) return;
        var arguments = ProcessRunner.SplitArguments(value);
        var rootWrapper = Path.Combine(_workspace.Root, "mvnw.cmd");
        var directory = Path.GetDirectoryName(FileService.SafePath(_workspace.Root, module.Path))!;
        var localWrapper = Path.Combine(directory, "mvnw.cmd");
        var executable = File.Exists(localWrapper) ? localWrapper : File.Exists(rootWrapper) ? rootWrapper : "mvn.cmd";
        // PowerShell encoded input quotes each argument separately, including shell metacharacters.
        string Quote(string input) => "'" + input.Replace("'", "''") + "'";
        var script = "& " + Quote(executable) + " " + string.Join(' ', arguments.Select(Quote)) + "; exit $LASTEXITCODE";
        _commandBusy = true; _command?.Dispose(); _command = new();
        ShowOutput("Maven · " + value, "构建中… 输出将在进程结束后显示，可点击停止取消。运行上限 20 分钟。");
        try
        {
            var result = await ProcessRunner.RunAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], directory, cancellationToken: _command.Token, timeoutSeconds: 1200);
            ShowOutput("Maven · " + value, result.Combined); SetStatus($"Maven 退出码 {result.ExitCode}");
        }
        catch (OperationCanceledException) { ShowOutput("Maven", "构建已停止。"); }
        finally { _commandBusy = false; }
    });
}

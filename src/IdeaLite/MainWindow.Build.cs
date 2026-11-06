using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using IdeaLite.Core;

namespace IdeaLite;

public partial class MainWindow
{
    private sealed record MavenTask(MavenModule Module, string Goal);
    private readonly ConcurrentQueue<string> _outputChunks = new();
    private readonly DispatcherTimer _outputTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch _buildClock = new();
    private string? _lastMavenModule;
    private sealed record BuildIssue(string Path, int Line);
    private TreeViewItem? _buildRoot, _buildModule;
    private string _buildLineTail = "";
    private readonly HashSet<string> _buildErrors = [];
    private static readonly Regex BuildLocation = new(@"(?<path>(?:[A-Za-z]:[\\/]|/).+?\.java):\[(?<line>\d+),\d+\]", RegexOptions.CultureInvariant);

    private void PopulateMavenTree()
    {
        MavenList.Items.Clear();
        foreach (var module in _workspace?.Maven ?? [])
        {
            var item = new TreeViewItem { Header = FileHeader(module.Artifact, "Maven", module.Path), Tag = module, IsExpanded = module.Path == "pom.xml" };
            if (module.Error != null) UiText.Set((TextBlock)((StackPanel)item.Header).Children[1], "Text", "解析失败");
            var lifecycle = new TreeViewItem { Header = "生命周期", IsExpanded = false }.Localize("Header");
            foreach (var goal in new[] { "clean", "validate", "compile", "test", "package", "verify", "install", "deploy" })
                lifecycle.Items.Add(new TreeViewItem { Header = FileHeader(goal, "Run", goal), Tag = new MavenTask(module, goal) });
            item.Items.Add(lifecycle);
            var dependencies = new TreeViewItem { Header = $"依赖 ({module.Dependencies.Count})" }.Localize("Header");
            foreach (var dependency in module.Dependencies)
            {
                var entry = new TreeViewItem(); UiText.Set(entry, "Header", () => DependencyLabel(dependency)); dependencies.Items.Add(entry);
            }
            item.Items.Add(dependencies);
            MavenList.Items.Add(item);
        }
    }

    private static string DependencyLabel(MavenDependency dependency) =>
        $"{dependency.Group}:{dependency.Artifact}:{(dependency.Version == "由父 POM / BOM 管理" ? UiText.Current.Translate(dependency.Version) : dependency.Version)} [{dependency.Scope}]";

    private async Task RunMavenAsync(MavenModule module, string value)
    {
        if (_workspace == null || _commandBusy || !await CheckUnsavedBuffersAsync()) return;
        var arguments = new List<string> { "-B", "-V", "-Dstyle.color=never" };
        if (!string.IsNullOrWhiteSpace(_layout.MavenSettings)) { arguments.Add("-s"); arguments.Add(_layout.MavenSettings); }
        if (!string.IsNullOrWhiteSpace(_layout.MavenRepository)) arguments.Add("-Dmaven.repo.local=" + _layout.MavenRepository);
        arguments.AddRange(ProcessRunner.SplitArguments(value));
        var wrapperName = _layout.MavenInWsl ? "mvnw" : "mvnw.cmd";
        var rootWrapper = Path.Combine(_workspace.Root, wrapperName);
        var directory = Path.GetDirectoryName(FileService.SafePath(_workspace.Root, module.Path))!;
        var localWrapper = Path.Combine(directory, wrapperName);
        var executable = !string.IsNullOrWhiteSpace(_layout.MavenExecutable) ? _layout.MavenExecutable :
            File.Exists(localWrapper) ? localWrapper : File.Exists(rootWrapper) ? rootWrapper : _layout.MavenInWsl ? "mvn" : "mvn.cmd";
        if (_layout.MavenInWsl && Path.IsPathRooted(executable) && executable.Length > 1 && executable[1] == ':') executable = WslProcess.LinuxPath(executable);
        _layout.MavenGoals = value; _lastMavenModule = module.Path;
        _layout.MavenGoalsByProject[_workspace.Root] = value;
        RunConfigLabel.Text = module.Artifact + " · " + value;
        _commandBusy = true; _command?.Dispose(); _command = new();
        ShowOutput($"{module.Artifact} · {value}", "");
        BuildTree.Items.Clear(); _buildErrors.Clear(); _buildLineTail = "";
        _buildRoot = new TreeViewItem { Header = FileHeader(module.Artifact, "Maven", value), IsExpanded = true };
        _buildModule = _buildRoot; BuildTree.Items.Add(_buildRoot);
        while (_outputChunks.TryDequeue(out _)) { }
        _buildClock.Restart();
        BuildProgress.IsIndeterminate = true; BuildProgress.Visibility = Visibility.Visible;
        CancelBuildButton.IsEnabled = true; UiText.Set(BuildResult, "Text", "构建中");
        _outputTimer.Start();
        SetStatus($"Maven · {value} · 运行中");
        try
        {
            CommandResult result;
            if (_layout.MavenInWsl)
            {
                var environment = string.IsNullOrWhiteSpace(_layout.JavaHome) ? "" : $"export JAVA_HOME={WslProcess.Quote(_layout.JavaHome)}; export PATH=\"$JAVA_HOME/bin:$PATH\"; ";
                var script = environment + "exec " + WslProcess.Quote(executable) + " " + string.Join(' ', arguments.Select(WslProcess.Quote));
                result = await WslProcess.RunAsync(script, directory, _command.Token, chunk => _outputChunks.Enqueue(chunk));
            }
            else
            {
                var script = ProcessRunner.PowerShellInvocation(executable, arguments, _layout.JavaHome);
                result = await ProcessRunner.RunAsync(ProcessRunner.PowerShellExecutable, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", script], directory,
                    cancellationToken: _command.Token, timeoutSeconds: 1200, onOutput: chunk => _outputChunks.Enqueue(chunk));
            }
            UiText.Set(BuildResult, "Text", result.ExitCode == 0 ? "构建成功" : $"构建失败 · 退出码 {result.ExitCode}");
        }
        catch (OperationCanceledException) { UiText.Set(BuildResult, "Text", "已停止"); }
        catch (Exception ex) { UiText.Set(BuildResult, "Text", "构建失败"); _outputChunks.Enqueue(ex.Message + Environment.NewLine); }
        finally
        {
            _buildClock.Stop(); _outputTimer.Stop();
            do { FlushBuildOutput(); } while (!_outputChunks.IsEmpty);
            BuildProgress.IsIndeterminate = false; BuildProgress.Visibility = Visibility.Collapsed;
            CancelBuildButton.IsEnabled = false; _commandBusy = false;
            if (_buildLineTail.Length > 0) ParseBuildLine(_buildLineTail);
            SetStatus(() => UiText.Current.Format($"Maven · {value} · {UiText.Current.Translate(UiText.Source(BuildResult, TextBlock.TextProperty)?.ToString())} · {_buildClock.Elapsed.TotalSeconds:F1} 秒"));
        }
    }

    private void FlushBuildOutput()
    {
        var text = new System.Text.StringBuilder();
        // Batch chunks on the UI thread so a verbose build does not dispatch one update per line.
        while (text.Length < 128 * 1024 && _outputChunks.TryDequeue(out var chunk)) text.Append(chunk);
        if (text.Length > 0)
        {
            OutputEditor.AppendText(text.ToString());
            var lines = (_buildLineTail + text).Split('\n');
            foreach (var line in lines[..^1]) ParseBuildLine(line.TrimEnd('\r'));
            _buildLineTail = lines[^1].Length <= 16384 ? lines[^1] : lines[^1][^16384..];
            const int visibleLimit = 1024 * 1024;
            if (OutputEditor.Document.TextLength > visibleLimit)
            {
                var line = OutputEditor.Document.GetLineByOffset(OutputEditor.Document.TextLength - visibleLimit);
                OutputEditor.Document.Remove(0, line.EndOffset);
            }
            if (FollowOutput.IsChecked == true) OutputEditor.ScrollToEnd();
        }
        BuildElapsed.Text = _buildClock.Elapsed.ToString(@"mm\:ss");
    }

    private void ClearOutputClick(object sender, RoutedEventArgs e) => OutputEditor.Clear();

    private void ParseBuildLine(string line)
    {
        if (_buildRoot == null) return;
        const string modulePrefix = "[INFO] Building ";
        if (line.StartsWith(modulePrefix, StringComparison.Ordinal))
        {
            _buildModule = new TreeViewItem { Header = FileHeader(line[modulePrefix.Length..], "Maven", line), IsExpanded = true };
            _buildRoot.Items.Add(_buildModule);
        }
        else if (line.StartsWith("[ERROR]", StringComparison.Ordinal) && line.Length > 8 && _buildErrors.Count < 200 && _buildErrors.Add(line))
        {
            var location = BuildLocation.Match(line);
            var item = new TreeViewItem { Header = new TextBlock { Text = line[7..].Trim(), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = System.Windows.Media.Brushes.Salmon }, ToolTip = line };
            if (location.Success && int.TryParse(location.Groups["line"].Value, out int number))
                item.Tag = new BuildIssue(location.Groups["path"].Value, number);
            (_buildModule ?? _buildRoot).Items.Add(item);
        }
    }

    private async void BuildIssueDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TreeItemAt(e.OriginalSource)?.Tag is not BuildIssue issue || _workspace == null) return;
        e.Handled = true;
        var path = issue.Path;
        if (path.StartsWith("/mnt/", StringComparison.Ordinal) && path.Length > 7 && path[6] == '/')
            path = char.ToUpperInvariant(path[5]) + ":\\" + path[7..].Replace('/', '\\');
        await Guard(() => OpenFileAsync(Relative(path), issue.Line));
    }

    private void BuildSettingsClick()
    {
        if (!Dialogs.BuildSettings(this, _layout)) return;
        if (_workspace != null) _layout.MavenGoalsByProject[_workspace.Root] = _layout.MavenGoals;
        _layout.Save(); RunConfigLabel.Text = "Maven · " + _layout.MavenGoals;
    }

    private async Task RunConfiguredMavenAsync(string? goals = null)
    {
        var module = goals == null ? _workspace?.Maven.FirstOrDefault(module => module.Path == _lastMavenModule) : null;
        module ??= _workspace?.Maven.FirstOrDefault();
        if (module != null) await RunMavenAsync(module, goals ?? _layout.MavenGoals);
    }

    private async void RunLastMavenClick(object sender, RoutedEventArgs e) => await Guard(() => RunConfiguredMavenAsync());

    private void RunConfigClick(object sender, RoutedEventArgs e)
    {
        var menu = PopupMenu(RunConfigButton);
        MenuAction(menu, "编辑任务与参数…", () => { BuildSettingsClick(); return Task.CompletedTask; });
        MenuAction(menu, "运行其他 Maven 任务…", () => { MavenClick(this, new RoutedEventArgs()); return Task.CompletedTask; });
        menu.IsOpen = true;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using IdeaLite;
using IdeaLite.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Pump();
        foreach (var startupWindow in app.Windows.Cast<Window>().ToArray()) startupWindow.Close();
        app.MainWindow = null;
        UiText.Current.Language = "en";
        if (args.Length == 2 && args[0] == "--render-code-preview")
        {
            EditorVisualChecks.Render(args[1], Pump); app.Shutdown(); return 0;
        }
        if (args.Length == 2 && args[0] == "--render-ai-settings")
        {
            AiUiChecks.RenderSettings(args[1], Pump); app.Shutdown(); return 0;
        }
        if (args.Length == 3 && args[0] == "--verify-project")
        {
            Environment.SetEnvironmentVariable("PATH", Environment.SystemDirectory);
            int result = VerifyMaven(args[1], "--git-only", args[2]); app.Shutdown(); return result;
        }
        if (args.Length == 4 && args[0] == "--verify-maven")
        {
            int result = VerifyMaven(args[1], args[2], args[3]); app.Shutdown(); return result;
        }
        if (args.Length is 3 or 4 && args[0] is "--render-preview" or "--render-preview-zh")
        {
            UiText.Current.Language = args[0] == "--render-preview-zh" ? "zh-CN" : "en";
            RenderPreview(args[1], args[2], args.Length == 4 ? args[3] : null); app.Shutdown(); return 0;
        }
        int failed = 0, passed = 0;
        void Check(string name, Action check)
        {
            var filter = args.FirstOrDefault(argument => argument.StartsWith("--filter=", StringComparison.Ordinal));
            if (filter != null && !name.Contains(filter[9..], StringComparison.OrdinalIgnoreCase)) return;
            try { check(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
            finally { UiText.Current.Language = "en"; }
        }
        EditorVisualChecks.Run(Check, Pump);
        TerminalChecks.Run(Check, Pump);
        AiUiChecks.Run(Check, Pump);
        Check("Translation templates preserve placeholders and user data in both languages", () =>
        {
            using var stream = typeof(UiText).Assembly.GetManifestResourceStream("IdeaLite.Strings.en.json")!;
            var catalog = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            foreach (var pair in catalog)
            {
                Require(!string.IsNullOrWhiteSpace(pair.Value), "Empty translation: " + pair.Key);
                string[] Fields(string value) => System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+\}").Select(match => match.Value).Order().ToArray();
                Require(Fields(pair.Key).SequenceEqual(Fields(pair.Value)), "Translation changed placeholders: " + pair.Key);
            }
            const string path = @"D:\提交\项目\本地完成.java";
            Require(UiText.Current.Translate($"已保存 {path}") == "Saved " + path, "Translation changed a path argument");
            Require(UiText.Current.Translate(path) == path, "Translation changed a raw path");
            UiText.Current.Language = "zh-CN";
            Require(UiText.Current.Translate("Saved " + path) == "已保存 " + path, "Reverse translation changed a path argument");
            UiText.Current.Language = "en";
        });
        Check("Language menu switches live and persists without changing editor or commit state", () =>
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "idea-lite-language-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "README.md"), string.Join('\n', Enumerable.Range(1, 200).Select(line => "中文项目 提交 " + line)));
            var workbench = new MainWindow(null);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            void Wait(Task task)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 20) { Pump(); Thread.Sleep(10); }
                Require(task.IsCompleted, "Language fixture timed out"); task.GetAwaiter().GetResult();
            }
            try
            {
                workbench.Show();
                Wait((Task)typeof(MainWindow).GetMethod("OpenWorkspaceAsync", flags)!.Invoke(workbench, [root])!);
                var editor = (TextEditor)workbench.FindName("Editor");
                editor.Document.Insert(0, "// 未保存的中文编辑\n"); editor.CaretOffset = 80; editor.ScrollToLine(60); Pump();
                var document = editor.Document; var text = editor.Text; int caret = editor.CaretOffset; double scroll = editor.VerticalOffset;
                var commit = (TextBox)workbench.FindName("CommitMessage"); commit.Text = "提交 这段中文必须原样保留";
                var output = (TextEditor)workbench.FindName("OutputEditor"); output.Text = "[INFO] 项目 提交：原始构建日志";
                var choice = new CommitChoice { Included = true };
                var changes = new ListBox { ItemsSource = new[] { new ChangeItem(new("项目.java", null, ' ', 'M'), "更改", false, choice) } };
                ((ListBox)workbench.FindName("ChangesList")).ItemsSource = changes.ItemsSource;
                var menu = (MenuItem)typeof(MainWindow).GetMethod("LanguageMenu", flags)!.Invoke(workbench, null)!;
                ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Require(((Button)workbench.FindName("ProjectRail")).ToolTip as string == "项目 Project · Alt+1", "Chinese tooltip was not applied");
                Require(WorkbenchLayout.Load().Language == "zh-CN", "Chinese preference was not saved");
                ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Require(((Button)workbench.FindName("ProjectRail")).ToolTip as string == "Project · Alt+1", "English tooltip was not applied");
                Require(WorkbenchLayout.Load().Language == "en", "English preference was not saved");
                Require(ReferenceEquals(document, editor.Document) && editor.Text == text && editor.CaretOffset == caret && Math.Abs(editor.VerticalOffset - scroll) < 1, "Language switch changed the active editor");
                Require(commit.Text == "提交 这段中文必须原样保留" && choice.Included, "Language switch changed commit input or selection");
                Require(output.Text == "[INFO] 项目 提交：原始构建日志", "Language switch changed raw console output");
                Require((bool)typeof(MainWindow).GetField("_active", flags)!.GetValue(workbench)!.GetType().GetProperty("Dirty")!.GetValue(typeof(MainWindow).GetField("_active", flags)!.GetValue(workbench))!, "Language switch discarded the dirty state");
                Wait((Task)typeof(MainWindow).GetMethod("SaveAsync", flags)!.Invoke(workbench, [false])!);
            }
            finally
            {
                UiText.Current.Language = "en";
                var active = typeof(MainWindow).GetField("_active", flags)!.GetValue(workbench);
                active?.GetType().GetProperty("Dirty")?.SetValue(active, false);
                workbench.Close(); System.IO.Directory.Delete(root, true);
            }
        });
        Check("Search, merge and confirmation dialogs follow the selected language", () =>
        {
            var owner = new MainWindow(null); owner.Show();
            var snapshot = new WorkspaceSnapshot("", [], [], [], [], false);
            var search = new SearchWindow(owner, snapshot, "all");
            var merge = new MergeWindow(owner, "中文项目.java", new GitDiff("// 本地", "// 传入", "", ""), "// 原样保留", new EditorStyle(false, 4, "\n", Source: "test"), _ => Task.CompletedTask);
            var find = new SearchLocalization();
            try
            {
                ((TextBox)search.FindName("Query")).Text = "原始中文查询";
                Require(search.Title == "Search Everywhere" && (string)((Button)merge.FindName("ApplyButton")).Content == "Apply and Stage", "English dialog text was missing");
                UiText.Current.Language = "zh-CN"; Pump();
                Require(search.Title == "随处搜索" && (string)((Button)merge.FindName("ApplyButton")).Content == "应用并暂存" && find.MatchCaseText == "区分大小写", "Chinese dialog text was missing");
                Require(((TextBox)search.FindName("Query")).Text == "原始中文查询" && ((TextEditor)merge.FindName("ResultEditor")).Text == "// 原样保留", "Dialog content was translated");
                foreach (var language in new[] { "en", "zh-CN" })
                {
                    UiText.Current.Language = language;
                    Exception? failure = null;
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                    {
                        var dialog = app.Windows.Cast<Window>().Single(window => window.Title == UiText.Current.Translate("Idea Lite · 确认操作"));
                        try
                        {
                            var buttons = ((StackPanel)((DockPanel)dialog.Content).Children[0]).Children.OfType<Button>().ToArray();
                            Require((string)buttons[0].Content == (language == "en" ? "No" : "否") && buttons[0].IsDefault, "Confirmation default or language changed");
                            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        catch (Exception error) { failure = error; dialog.Close(); }
                    });
                    Require(!Dialogs.Confirm(owner, "存在未保存的文档。继续会丢弃这些编辑，是否继续？"), "No confirmed a destructive action");
                    if (failure != null) throw failure;
                }
            }
            finally { UiText.Current.Language = "en"; search.Close(); merge.Close(); owner.Close(); }
        });
        Check("Dark context menus and nested popup surfaces", () =>
        {
            var menu = new ContextMenu();
            var item = new MenuItem { Header = "文件", InputGestureText = "Ctrl+O" };
            item.Items.Add(new MenuItem { Header = "打开项目" });
            menu.Items.Add(item);
            menu.Style = (Style)app.FindResource(typeof(ContextMenu));
            item.Style = (Style)app.FindResource(typeof(MenuItem));
            menu.ApplyTemplate(); item.ApplyTemplate();
            var popup = item.Template.FindName("PART_Popup", item) as Popup ?? throw new Exception("Missing submenu popup");
            var border = popup.Child as Border ?? throw new Exception("Missing submenu surface");
            Require((border.Background as SolidColorBrush)?.Color == Color.FromRgb(23, 24, 25), "Submenu background must follow the dark palette");
            Require((item.Foreground as SolidColorBrush)?.Color == Color.FromRgb(223, 225, 229), "Menu foreground must stay readable");
        });
        Check("Embedded editor font resolves without a system font installation", () =>
        {
            var editor = new TextEditor(); EditorSupport.Configure(editor);
            Require(new Typeface(editor.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var font), "Editor typeface did not resolve");
            Require(font.FontUri.ToString().Contains("IdeaLite;component/Assets/Fonts/", StringComparison.OrdinalIgnoreCase), "Editor fell back to a system font");
        });
        Check("File checkboxes update the commit selection in both directions", () =>
        {
            var workbench = new MainWindow(null);
            var list = (ListBox)workbench.FindName("ChangesList");
            var choice = new CommitChoice();
            var item = new ChangeItem(new("Example.java", null, ' ', 'M'), "更改", false, choice);
            var row = (FrameworkElement)list.ItemTemplate.LoadContent();
            row.DataContext = item;
            row.Measure(new Size(350, 30)); row.Arrange(new Rect(0, 0, 350, 30)); Pump();
            var checkbox = ((Grid)row).Children.OfType<CheckBox>().Single();
            checkbox.IsChecked = true; Pump();
            Require(choice.Included, "Checked file was not included");
            choice.Included = false; Pump();
            Require(checkbox.IsChecked == false, "Selection model did not update the checkbox");
        });
        Check("Deferred diff scrolling follows the input pane without feedback", () =>
        {
            var left = new TextEditor(); var right = new TextEditor();
            EditorSupport.Configure(left); EditorSupport.Configure(right);
            left.Options.AllowScrollBelowDocument = right.Options.AllowScrollBelowDocument = false;
            left.Text = right.Text = string.Join('\n', Enumerable.Range(0, 400).Select(i => "line " + i));
            var host = new Grid { Width = 1000, Height = 300 };
            host.ColumnDefinitions.Add(new()); host.ColumnDefinitions.Add(new());
            host.Children.Add(left); host.Children.Add(right); Grid.SetColumn(right, 1);
            void Layout() { host.Measure(new Size(1000, 300)); host.Arrange(new Rect(0, 0, 1000, 300)); host.UpdateLayout(); Pump(); }
            Layout();
            _ = new DiffScrollSynchronizer(left, right);
            void Drive(TextEditor editor, double offset)
            {
                editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                editor.ScrollToVerticalOffset(offset);
                for (int i = 0; i < 4; i++) Layout();
            }
            Drive(left, 230);
            Require(left.VerticalOffset > 100, "Scroll fixture did not move");
            Require(Math.Abs(left.VerticalOffset - right.VerticalOffset) < 1, "Right pane failed to follow");
            double stable = left.VerticalOffset;
            for (int i = 0; i < 8; i++) Layout();
            Require(Math.Abs(left.VerticalOffset - stable) < 1, "Deferred follower events moved the driver");
            Drive(right, 460);
            Require(Math.Abs(left.VerticalOffset - right.VerticalOffset) < 1, "Changing the input pane broke synchronization");
        });
        Check("README opening, scrolling and switching documents", () =>
        {
            var workbench = new MainWindow(null);
            workbench.Show();
            var method = typeof(MainWindow).GetMethod("OpenWorkspaceAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var shellId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            int legacyResult = SHCreateItemFromParsingName(root.Replace('\\', '/'), 0, ref shellId, out var legacyItem);
            if (legacyItem != 0) System.Runtime.InteropServices.Marshal.Release(legacyItem);
            int shellResult = SHCreateItemFromParsingName(System.IO.Path.GetFullPath(root.Replace('\\', '/')), 0, ref shellId, out var shellItem);
            if (shellItem != 0) System.Runtime.InteropServices.Marshal.Release(shellItem);
            Require(shellResult == 0, "Native project path was rejected by the Windows folder picker");
            Console.WriteLine($"Shell path parsing: Git separators=0x{legacyResult:X8}, native=0x{shellResult:X8}");
            var task = (Task)method.Invoke(workbench, [root])!;
            var host = (FrameworkElement)workbench.Content;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            void Layout()
            {
                host.Measure(new Size(1380, 860)); host.Arrange(new Rect(0, 0, 1380, 860)); host.UpdateLayout(); Pump();
            }
            while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 30) { Layout(); Thread.Sleep(10); }
            Require(task.IsCompleted, "Workspace opening timed out");
            task.GetAwaiter().GetResult();
            var editor = (TextEditor)workbench.FindName("Editor");
            Require(editor.Text.Contains("Idea Lite"), "README was not opened");
            for (int line = 1; line < editor.Document.LineCount; line += 7) { editor.ScrollToLine(line); Layout(); }
            editor.CaretOffset = 0; editor.ScrollToHome(); Layout();
            Require(editor.VerticalOffset < 1, "Editor did not return to the start");
            workbench.Close();
        });
        Check("Maven goals stay with their project when switching workspaces", () =>
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "idea-lite-project-goals-" + Guid.NewGuid().ToString("N"));
            var first = System.IO.Path.Combine(directory, "first"); var second = System.IO.Path.Combine(directory, "second");
            System.IO.Directory.CreateDirectory(first); System.IO.Directory.CreateDirectory(second);
            var workbench = new MainWindow(null);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var layout = (WorkbenchLayout)typeof(MainWindow).GetField("_layout", flags)!.GetValue(workbench)!;
            layout.MavenGoalsByProject[first] = "-pl first compile";
            void Open(string path)
            {
                var task = (Task)typeof(MainWindow).GetMethod("OpenWorkspaceAsync", flags)!.Invoke(workbench, [path])!;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 20) { Pump(); Thread.Sleep(10); }
                Require(task.IsCompleted, "Workspace switch timed out"); task.GetAwaiter().GetResult();
            }
            Open(first); Require(layout.MavenGoals == "-pl first compile", "Project task was not restored");
            Open(second); Require(layout.MavenGoals == "test", "Previous project's module arguments leaked into another workspace");
            Open(first); Require(layout.MavenGoals == "-pl first compile", "Returning to the project lost its task");
            workbench.Close(); System.IO.Directory.Delete(directory, true);
        });
        Check("Maven lifecycle streams, reports failure and cancels without blocking the dispatcher", () =>
        {
            var parent = System.IO.Directory.Exists(@"D:\Codex\work") ? @"D:\Codex\work" : System.IO.Path.GetTempPath();
            var directory = System.IO.Path.Combine(parent, "idea-lite-build-check-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var module = new MavenModule("pom.xml", "build-check", "example", "1", [], [], new Dictionary<string, string>());
            var workbench = new MainWindow(null);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(MainWindow).GetField("_workspace", flags)!.SetValue(workbench, new WorkspaceSnapshot(directory, [], [], [], [module], false));
            typeof(MainWindow).GetMethod("PopulateMavenTree", flags)!.Invoke(workbench, null);
            var modules = (TreeView)workbench.FindName("MavenList");
            var lifecycle = (TreeViewItem)((TreeViewItem)modules.Items[0]).Items[0];
            Require(lifecycle.Items.Count == 8, "Maven lifecycle goals are missing");
            var output = (TextEditor)workbench.FindName("OutputEditor");
            var result = (TextBlock)workbench.FindName("BuildResult");
            var cancel = (Button)workbench.FindName("CancelBuildButton");
            void Until(Func<bool> done)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (!done() && timer.Elapsed.TotalSeconds < 20) { Pump(); Thread.Sleep(10); }
                Require(done(), "Build check timed out");
            }
            Task Run(string body)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "mvnw.cmd"), "@echo off\r\n" + body);
                return (Task)typeof(MainWindow).GetMethod("RunMavenAsync", flags)!.Invoke(workbench, [module, "test"])!;
            }
            string ping = System.IO.Path.Combine(Environment.SystemDirectory, "ping.exe");
            var running = Run($"echo [INFO] first\r\n\"{ping}\" -n 3 127.0.0.1 >nul\r\necho [INFO] last\r\nexit /b 0\r\n");
            Until(() => output.Text.Contains("first"));
            Require(!running.IsCompleted, "Build output was buffered until exit");
            Require(cancel.IsEnabled, "Stop button was disabled during the build");
            Until(() => running.IsCompleted); running.GetAwaiter().GetResult();
            Require(output.Text.Contains("last") && result.Text == UiText.Current.Translate("构建成功"), "Build completion lost output or status");
            var java = System.IO.Path.Combine(directory, "Example.java");
            System.IO.File.WriteAllText(java, "class Example {\n    int value;\n}\n");
            running = Run($"echo [ERROR] {java}:[2,5] fixture failure\r\nexit /b 7\r\n");
            Until(() => running.IsCompleted); running.GetAwaiter().GetResult();
            Require(result.Text == UiText.Current.Translate("构建失败 · 退出码 7") && output.Text.Contains("fixture failure"), "Nonzero exit was reported as success");
            var buildTree = (TreeView)workbench.FindName("BuildTree");
            Require(((TreeViewItem)buildTree.Items[0]).Items.OfType<TreeViewItem>().Any(item => item.Tag != null), "Compiler file/line was not captured as a navigation target");
            var errorItem = ((TreeViewItem)buildTree.Items[0]).Items.OfType<TreeViewItem>().First(item => item.Tag != null);
            buildTree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Control.MouseDoubleClickEvent, Source = errorItem });
            var editor = (TextEditor)workbench.FindName("Editor");
            Until(() => editor.Text.Contains("class Example"));
            Require(editor.TextArea.Caret.Line == 2, "Compiler error did not navigate to the reported source line");
            running = Run($"echo [INFO] waiting\r\n\"{ping}\" -n 30 127.0.0.1 >nul\r\necho should-not-run\r\n");
            Until(() => output.Text.Contains("waiting"));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => running.IsCompleted); running.GetAwaiter().GetResult();
            Require(result.Text == UiText.Current.Translate("已停止") && !cancel.IsEnabled && !output.Text.Contains("should-not-run"), "Stop failed to terminate the build tree");
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "mvnw.cmd"), "@echo off\r\necho lifecycle-click\r\nexit /b 0\r\n");
            modules.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Control.MouseDoubleClickEvent, Source = lifecycle.Items[3] });
            Until(() => output.Text.Contains("lifecycle-click") && !(bool)typeof(MainWindow).GetField("_commandBusy", flags)!.GetValue(workbench)!);
            Require(result.Text == UiText.Current.Translate("构建成功"), "Double-clicking a nested lifecycle goal did not run Maven");
            workbench.Close();
            System.IO.Directory.Delete(directory, true);
        });
        Console.WriteLine($"UI regression checks: {passed} passed, {failed} failed.");
        app.Shutdown();
        return failed == 0 ? 0 : 1;
    }
    private static int VerifyMaven(string project, string goals, string report)
    {
        var workbench = new MainWindow(null);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        workbench.Show();
        var host = (FrameworkElement)workbench.Content;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        double last = 0, maxGap = 0;
        void Tick()
        {
            host.Measure(new Size(1380, 860)); host.Arrange(new Rect(0, 0, 1380, 860)); host.UpdateLayout(); Pump();
            double current = timer.Elapsed.TotalMilliseconds; maxGap = Math.Max(maxGap, current - last); last = current;
            Thread.Sleep(10);
        }
        void Wait(Task task, int seconds)
        {
            var start = timer.Elapsed;
            while (!task.IsCompleted && (timer.Elapsed - start).TotalSeconds < seconds) Tick();
            Require(task.IsCompleted, "Maven verification timed out"); task.GetAwaiter().GetResult();
        }
        Wait((Task)typeof(MainWindow).GetMethod("OpenWorkspaceAsync", flags)!.Invoke(workbench, [project])!, 180);
        var snapshot = (WorkspaceSnapshot)typeof(MainWindow).GetField("_workspace", flags)!.GetValue(workbench)!;
        if (goals == "--git-only")
        {
            var git = typeof(MainWindow).GetField("_git", flags)!.GetValue(workbench) as GitService;
            var gitReport = new { Root = git?.Root, Branch = ((TextBlock)workbench.FindName("BranchLabel")).Text, Executable = GitService.ExecutablePath,
                IsRepository = git != null, ChangeCount = ((ListBox)workbench.FindName("ChangesList")).Items.Count };
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(report))!);
            System.IO.File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(gitReport, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(gitReport)); workbench.Close(); return git != null ? 0 : 1;
        }
        var module = snapshot.Maven.FirstOrDefault(module => module.Path == "pom.xml") ?? snapshot.Maven.First();
        var output = (TextEditor)workbench.FindName("OutputEditor");
        var resultLabel = (TextBlock)workbench.FindName("BuildResult");
        var build = (Task)typeof(MainWindow).GetMethod("RunMavenAsync", flags)!.Invoke(workbench, [module, goals])!;
        double started = timer.Elapsed.TotalMilliseconds; last = started; maxGap = 0;
        double? firstOutput = null;
        while (!build.IsCompleted && timer.Elapsed.TotalMilliseconds - started < 1200_000)
        {
            Tick();
            if (output.Document.TextLength > 0 && firstOutput == null) firstOutput = timer.Elapsed.TotalMilliseconds - started;
        }
        Require(build.IsCompleted, "Maven verification exceeded 20 minutes"); build.GetAwaiter().GetResult(); Tick();
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(report))!;
        System.IO.Directory.CreateDirectory(directory);
        var summary = new { Project = project, Goals = goals, Result = resultLabel.Text, ElapsedMs = timer.Elapsed.TotalMilliseconds - started, FirstOutputMs = firstOutput, MaxDispatcherGapMs = maxGap,
            OutputCharacters = output.Document.TextLength, StreamingBeforeExit = firstOutput.HasValue,
            GitBranch = ((TextBlock)workbench.FindName("BranchLabel")).Text };
        System.IO.File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        System.IO.File.WriteAllText(System.IO.Path.ChangeExtension(report, ".log"), output.Text);
        // Capture the output pane with synthetic/offline or explicitly selected local build data only.
        var pane = (FrameworkElement)workbench.FindName("BottomPane");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(pane), null, new Rect(0, 0, 1380, 430));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1380, 430, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var stream = System.IO.File.Create(System.IO.Path.ChangeExtension(report, ".png"))) encoder.Save(stream);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary));
        workbench.Close();
        return resultLabel.Text == UiText.Current.Translate("构建成功") ? 0 : 1;
    }
    private static void RenderPreview(string project, string destination, string? buildLog = null)
    {
        var workbench = new MainWindow(project);
        var host = (FrameworkElement)workbench.Content;
        void Layout()
        {
            host.Measure(new Size(1380, 860));
            host.Arrange(new Rect(0, 0, 1380, 860));
            host.UpdateLayout(); Pump();
        }
        void Until(Func<bool> done)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!done())
            {
                Layout();
                if (timer.Elapsed.TotalSeconds > 25) throw new Exception("Preview did not finish loading");
                Thread.Sleep(10);
            }
            Layout();
        }
        workbench.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var editor = (TextEditor)workbench.FindName("Editor");
        Until(() => !editor.IsReadOnly);
        ((Button)workbench.FindName("CommitRail")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var changes = (ListBox)workbench.FindName("ChangesList");
        Until(() => changes.Items.Count > 0);
        var item = changes.Items.Cast<ChangeItem>().First(c => c.Change.Name == "OrderService.java");
        item.Choice.Included = true; changes.SelectedItem = item;
        Button FindButton(DependencyObject root, string tooltip)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Button button && (button.ToolTip as string) == UiText.Current.Translate(tooltip)) return button;
                try { return FindButton(child, tooltip); } catch (InvalidOperationException) { }
            }
            throw new InvalidOperationException("Button not found: " + tooltip);
        }
        FindButton(host, "显示差异 · Ctrl+D").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => ((Grid)workbench.FindName("DiffPane")).Visibility == Visibility.Visible);
        ((Button)workbench.FindName("GitRail")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => ((ListBox)workbench.FindName("LogList")).Items.Count > 0 &&
            ((TreeView)workbench.FindName("HistoryFiles")).Items.Count > 0);
        for (int i = 0; i < 4; i++) Layout();
        if (buildLog != null)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var text = System.IO.File.ReadAllText(buildLog);
            typeof(MainWindow).GetMethod("ShowOutput", flags)!.Invoke(workbench, [System.IO.Path.GetFileName(project) + " · compile (captured log)", text, false]);
            var tree = (TreeView)workbench.FindName("BuildTree");
            var root = new TreeViewItem { Header = "Maven build", IsExpanded = true }; tree.Items.Add(root);
            typeof(MainWindow).GetField("_buildRoot", flags)!.SetValue(workbench, root);
            typeof(MainWindow).GetField("_buildModule", flags)!.SetValue(workbench, root);
            foreach (var line in text.Split('\n')) typeof(MainWindow).GetMethod("ParseBuildLine", flags)!.Invoke(workbench, [line.TrimEnd('\r')]);
            UiText.Set((TextBlock)workbench.FindName("BuildResult"), "Text", "构建失败 · 退出码 1");
            ((TextEditor)workbench.FindName("OutputEditor")).ScrollToEnd();
            for (int i = 0; i < 4; i++) Layout();
        }
        ((TextBlock)workbench.FindName("MemoryLabel")).Text = "";
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1380, 860, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!);
        using var output = System.IO.File.Create(destination);
        png.Save(output);
        Console.WriteLine("Rendered native workbench preview: " + destination);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid interfaceId, out nint item);
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}

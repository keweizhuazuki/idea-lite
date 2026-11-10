using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32.SafeHandles;
using Microsoft.Terminal.Wpf;
using IdeaLite;

internal static class TerminalChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(Action<string, Action> check, Action pump)
    {
        check("Embedded terminal keeps two native Git Bash sessions alive and closes owned processes", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "idea-lite terminal & 中文 ' " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "README.md"), "# Embedded terminal fixture\n");
            var inputConfig = Path.Combine(root, "inputrc");
            File.WriteAllText(inputConfig, "set enable-bracketed-paste on\n");
            string? previousInputConfig = Environment.GetEnvironmentVariable("INPUTRC");
            var owner = new MainWindow(null);
            var processes = new List<int>();
            var bracketedPaste = typeof(NativeTerminalSession).GetField("_bracketedPaste", Flags)!;
            void Until(Func<bool> done, string condition)
            {
                var timer = Stopwatch.StartNew(); while (!done() && timer.Elapsed.TotalSeconds < 25) { pump(); Thread.Sleep(10); }
                Require(done(), "Embedded terminal timed out waiting for " + condition); pump();
            }
            void Wait(Task task) { Until(() => task.IsCompleted, "an asynchronous operation"); task.GetAwaiter().GetResult(); }
            object[] Tabs() => ((IEnumerable)typeof(MainWindow).GetField("_terminals", Flags)!.GetValue(owner)!).Cast<object>().ToArray();
            static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
            object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Flags)!.Invoke(owner, args);
            bool Exited(int id) { try { using var process = Process.GetProcessById(id); return process.HasExited; } catch (ArgumentException) { return true; } }
            try
            {
                Environment.SetEnvironmentVariable("INPUTRC", inputConfig.Replace('\\', '/'));
                owner.Show();
                Wait((Task)Invoke("OpenWorkspaceAsync", root)!);
                Invoke("OpenTerminal"); Until(() => Tabs().Length == 1 && Property<TerminalControl>(Tabs()[0], "View").Rows > 0, "the first terminal view");
                Invoke("AddTerminal"); Until(() => Tabs().Length == 2 && Property<TerminalControl>(Tabs()[1], "View").Rows > 0, "the second terminal view");
                var tabs = Tabs(); var first = Property<NativeTerminalSession>(tabs[0], "Session"); var second = Property<NativeTerminalSession>(tabs[1], "Session");
                foreach (var session in new[] { first, second })
                {
                    var handle = (SafeProcessHandle?)typeof(NativeTerminalSession).GetField("_processHandle", Flags)!.GetValue(session);
                    Require(handle != null && !handle.IsInvalid, "Native ConPTY session did not start"); processes.Add((int)GetProcessId(handle!));
                }
                Require(processes.Distinct().Count() == 2 && processes.All(id => !Exited(id)), "Terminal tabs share a process or immediately exit");
                Require(((Grid)owner.FindName("TerminalPane")).Children.Count == 2, "Terminals were launched in external windows");
                var firstOutput = new StringBuilder(); var secondOutput = new StringBuilder();
                first.OutputReceived += (_, e) => { lock (firstOutput) firstOutput.Append(e.Data); };
                second.OutputReceived += (_, e) => { lock (secondOutput) secondOutput.Append(e.Data); };
                string Output(StringBuilder builder) { lock (builder) return builder.ToString(); }
                // Readline enables bracketed paste when each shell reaches its interactive prompt.
                Until(() => bracketedPaste.GetValue(first) is true, "the first Git Bash prompt");
                Until(() => bracketedPaste.GetValue(second) is true, "the second Git Bash prompt");
                Wait(first.WriteAsync("printf '\\033[32m__%s__中文\\033[0m\\n' 'ONE'; printf '__PWD:%s:END__\\n' \"$(pwd -P)\"\r", default).AsTask());
                Wait(second.WriteAsync("printf '__%s__\\n' 'TWO'\r", default).AsTask());
                Until(() => Output(firstOutput).Contains("__ONE__中文"), "the first session's Unicode output");
                Until(() => Output(secondOutput).Contains("__TWO__"), "the second session's output");
                Until(() => Output(firstOutput).Contains(":END__"), "the first session's working directory");
                Require(Output(firstOutput).Contains(Path.GetFileName(root)) && !Output(firstOutput).Contains("__TWO__"), "Terminal cwd or session isolation is wrong");
                Require(Output(firstOutput).Contains("\x1b[32m"), "Colored output was stripped from the terminal transport");
                Invoke("SelectTerminal", tabs[0]); pump(); Invoke("SelectTerminal", tabs[1]); pump();
                Wait(second.ResizeAsync(new TerminalSize(110, 28), default).AsTask());
                Wait(second.WriteAsync("PS1=$(printf '__%s__> ' 'READY')\r", default).AsTask());
                Until(() => Output(secondOutput).Contains("__READY__> "), "the customized prompt");
                int prompts = Output(secondOutput).Split("__READY__> ").Length;
                Wait(second.WriteAsync("printf '__%s__\\n' 'START'; sleep 20; printf '__%s__\\n' 'SHOULD_NOT_RUN'\r", default).AsTask());
                Until(() => Output(secondOutput).Contains("__START__"), "the foreground command to start");
                Wait(second.WriteAsync("\x03", default).AsTask());
                Until(() => Output(secondOutput).Split("__READY__> ").Length > prompts, "the prompt after Ctrl+C");
                Wait(second.WriteAsync("printf '__%s__\\n' 'AFTER_CANCEL'\r", default).AsTask());
                Until(() => Output(secondOutput).Contains("__AFTER_CANCEL__"), "output after Ctrl+C");
                Require(!Output(secondOutput).Contains("__SHOULD_NOT_RUN__"), "Ctrl+C failed to interrupt the foreground command");
                Until(() => Output(secondOutput).Split("__READY__> ").Length > prompts + 1, "the prompt before history navigation");
                Wait(second.WriteAsync("\x1b[A\r", default).AsTask());
                Until(() => Output(secondOutput).Split("__AFTER_CANCEL__").Length >= 3, "replayed command history");
                var shortcut = typeof(MainWindow).GetMethod("HandleTerminalShortcut", Flags)!;
                shortcut.Invoke(owner, [System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift]);
                Until(() => Tabs().Length == 3 && Property<TerminalControl>(Tabs()[2], "View").Rows > 0, "the shortcut-created terminal view");
                var third = Property<NativeTerminalSession>(Tabs()[2], "Session");
                var thirdHandle = (SafeProcessHandle)typeof(NativeTerminalSession).GetField("_processHandle", Flags)!.GetValue(third)!; processes.Add((int)GetProcessId(thirdHandle));
                shortcut.Invoke(owner, [System.Windows.Input.Key.W, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift]);
                Until(() => Exited(processes[2]), "the shortcut-created process to exit");
                Require(Tabs().Length == 2, "Terminal shortcuts did not create and close independent sessions");
                Invoke("CloseTerminal", tabs[0]); Until(() => Exited(processes[0]), "the first process to exit");
                Require(Tabs().Length == 1 && !Exited(processes[1]), "Closing one terminal ended another session");
                owner.Close(); Until(() => processes.All(Exited), "all owned processes to exit");
            }
            finally
            {
                Environment.SetEnvironmentVariable("INPUTRC", previousInputConfig);
                owner.Close();
                foreach (int id in processes)
                    if (!Exited(id)) { using var process = Process.GetProcessById(id); process.Kill(true); }
                Program.DeleteFixtureDirectory(root, pump);
            }
        });
        check("Embedded terminal paste follows the shell mode without executing bracketed multiline input", () =>
        {
            using var session = new NativeTerminalSession("unused", Path.GetTempPath());
            var publish = typeof(NativeTerminalSession).GetMethod("Publish", Flags)!;
            string Paste(string text) => (string)typeof(NativeTerminalSession).GetMethod("PreparePaste", Flags)!.Invoke(session, [text])!;
            Require(Paste("a\r\nb") == "a\rb", "Legacy terminal line endings were not normalized");
            publish.Invoke(session, ["\x1b[?20"]); publish.Invoke(session, ["04h"]);
            Require(Paste("a\r\nb") == "\x1b[200~a\nb\x1b[201~", "Bracketed paste lost multiline boundaries or split escape state");
            publish.Invoke(session, ["\x1b[?2004l"]); Require(Paste("a\nb") == "a\rb", "Exiting bracketed paste mode retained stale wrappers");
        });
    }
    [DllImport("kernel32.dll")]
    private static extern uint GetProcessId(SafeProcessHandle process);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}

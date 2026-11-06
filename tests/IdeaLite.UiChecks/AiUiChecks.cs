using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IdeaLite;
using IdeaLite.Core;

internal static class AiUiChecks
{
    public static void RenderSettings(string destination, Action pump)
    {
        var owner = new MainWindow(null); owner.Show();
        var dialog = new AiCommitSettingsWindow(owner, new WorkbenchLayout(), new AiCredentials("IdeaLite/Preview/" + Guid.NewGuid().ToString("N") + "/"));
        try
        {
            dialog.Show(); pump(); dialog.UpdateLayout();
            var content = (FrameworkElement)dialog.Content;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(dialog);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(destination); png.Save(output);
            Console.WriteLine("Rendered AI settings: " + destination);
        }
        finally { dialog.Close(); owner.Close(); }
    }

    public static void Run(Action<string, Action> check, Action pump)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Wait(Task task)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed.TotalSeconds < 20) { pump(); Thread.Sleep(10); }
            Require(task.IsCompleted, "AI window check timed out"); task.GetAwaiter().GetResult();
        }
        check("AI settings save isolated credentials, provider and language without plaintext keys in config", () =>
        {
            var credentials = new AiCredentials("IdeaLite/Tests/" + Guid.NewGuid().ToString("N") + "/");
            var owner = new MainWindow(null); owner.Show();
            var previous = WorkbenchLayout.Load();
            try
            {
                Require(credentials.Read(AiProvider.OpenAI) == "", "Missing credential should be empty");
                var settings = new WorkbenchLayout();
                var dialog = new AiCommitSettingsWindow(owner, settings, credentials);
                Exception? failure = null;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    try
                    {
                        var boxes = Descendants(dialog).OfType<PasswordBox>().ToArray();
                        Require(boxes.Length == 2, "Both provider keys must be masked");
                        boxes[0].Password = "fixture-openai-key"; boxes[1].Password = "fixture-deepseek-key";
                        var combos = Descendants(dialog).OfType<ComboBox>().ToArray();
                        var prompt = Descendants(dialog).OfType<TextBox>().Single(input => input.Name == "CommitPromptInput");
                        prompt.Text = "temporary custom prompt";
                        Descendants(dialog).OfType<Button>().Single(button => (button.Content as string) == "Restore Default Prompt").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(prompt.Text == AiCommitService.DefaultPrompt, "Restore Default did not reset the editable prompt");
                        // A literal UI word must remain unmodified in the resolved prompt preview.
                        prompt.Text = "项目";
                        Exception? previewFailure = null;
                        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                        {
                            var preview = Application.Current.Windows.Cast<Window>().Single(window => window.Title == "Resolved System Prompt");
                            try { Require(Descendants(preview).OfType<TextBox>().Single().Text == "项目", "Prompt preview translated user instructions"); }
                            catch (Exception error) { previewFailure = error; }
                            finally { preview.Close(); }
                        });
                        Descendants(dialog).OfType<Button>().Single(button => (button.Content as string) == "Preview Resolved Prompt").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (previewFailure != null) throw previewFailure;
                        prompt.Text = "Custom instructions 中文 {task_id} {language}";
                        combos[0].IsDropDownOpen = true; pump();
                        var popup = (System.Windows.Controls.Primitives.Popup)combos[0].Template.FindName("PART_Popup", combos[0]);
                        Require(popup.IsOpen && ((Border)popup.Child).Background == (Brush)owner.FindResource("Raised"), "Provider popup lost its dark surface");
                        ((ComboBoxItem)combos[0].ItemContainerGenerator.ContainerFromIndex(1)).IsSelected = true;
                        combos[0].IsDropDownOpen = false; combos[1].SelectedIndex = 1;
                        UiText.Current.Language = "zh-CN"; pump(); Require(dialog.Title == "AI 提交设置", "Settings must switch to Chinese");
                        UiText.Current.Language = "en"; pump(); Require(dialog.Title == "AI Commit Settings", "Settings must switch to English");
                        Require(prompt.Text == "Custom instructions 中文 {task_id} {language}", "Changing UI language modified custom instructions");
                        Descendants(dialog).OfType<Button>().Single(button => (button.Content as string) == "Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception error) { failure = error; dialog.Close(); }
                });
                Require(dialog.ShowDialog() == true, "Settings did not save"); if (failure != null) throw failure;
                Require(credentials.Read(AiProvider.OpenAI) == "fixture-openai-key" && credentials.Read(AiProvider.DeepSeek) == "fixture-deepseek-key", "Credential round trip failed");
                var loaded = WorkbenchLayout.Load();
                Require(loaded.CommitAiProvider == AiProvider.DeepSeek && loaded.CommitAiLanguage == "zh-CN", "AI preferences were not saved");
                Require(loaded.CommitAiPrompt == "Custom instructions 中文 {task_id} {language}", "Custom prompt was not persisted");
                var json = System.Text.Json.JsonSerializer.Serialize(loaded);
                Require(!json.Contains("fixture-openai-key") && !json.Contains("fixture-deepseek-key"), "A key entered the workbench config");
                credentials.Save(AiProvider.OpenAI, ""); Require(credentials.Read(AiProvider.OpenAI) == "", "Blank key did not delete credential");
                Require(credentials.Read(AiProvider.DeepSeek) == "fixture-deepseek-key", "Deleting one provider changed another");
                var canceled = new AiCommitSettingsWindow(owner, loaded, credentials);
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    Require(Descendants(canceled).OfType<TextBox>().Single(input => input.Name == "CommitPromptInput").Text == loaded.CommitAiPrompt, "Saved prompt did not reopen");
                    Descendants(canceled).OfType<TextBox>().Single(input => input.Name == "CommitPromptInput").Text = "discard these edits";
                    Descendants(canceled).OfType<PasswordBox>().Last().Password = "unsaved-key"; canceled.Close();
                });
                Require(canceled.ShowDialog() != true && credentials.Read(AiProvider.DeepSeek) == "fixture-deepseek-key", "Cancel persisted a key");
                Require(WorkbenchLayout.Load().CommitAiPrompt == "Custom instructions 中文 {task_id} {language}", "Cancel persisted prompt edits");
            }
            finally
            {
                credentials.Save(AiProvider.OpenAI, ""); credentials.Save(AiProvider.DeepSeek, ""); owner.Close(); previous.Save(); UiText.Current.Language = "en";
            }
        });
        check("AI button generates a draft, cancels and protects in-flight edits", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "idea-lite-ai-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var credentials = new AiCredentials("IdeaLite/Tests/" + Guid.NewGuid().ToString("N") + "/");
            var previous = WorkbenchLayout.Load();
            var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            string? sentPrompt = null;
            using var http = new HttpClient(new Stub(async (request, token) =>
            {
                using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                sentPrompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
                Interlocked.Increment(ref calls); return await pending.Task.WaitAsync(token);
            }));
            var owner = new MainWindow(null, new AiCommitService(http), credentials);
            try
            {
                credentials.Save(AiProvider.OpenAI, "fixture-openai-key"); credentials.Save(AiProvider.DeepSeek, "fixture-deepseek-key");
                var settings = (WorkbenchLayout)typeof(MainWindow).GetField("_layout", flags)!.GetValue(owner)!;
                settings.CommitAiProvider = AiProvider.OpenAI;
                settings.CommitAiPrompt = "UI custom prompt: {task_id} {language}";
                var git = new GitService(root);
                Wait(git.RunAsync(["init", "-b", "AIDF-100"]));
                File.WriteAllText(Path.Combine(root, "README.md"), "new content\n");
                owner.Show(); Wait((Task)typeof(MainWindow).GetMethod("OpenWorkspaceAsync", flags)!.Invoke(owner, [root])!);
                var choices = (Dictionary<string, CommitChoice>)typeof(MainWindow).GetField("_commitChoices", flags)!.GetValue(owner)!;
                choices["README.md"].Included = true;
                var draft = (TextBox)owner.FindName("CommitMessage");
                var button = (Button)owner.FindName("AiGenerateButton");
                Task Generate() => (Task)typeof(MainWindow).GetMethod("GenerateCommitMessageAsync", flags)!.Invoke(owner, null)!;
                var generation = Generate();
                Until(() => calls == 1, pump);
                Require(sentPrompt == "UI custom prompt: AIDF-100 English", "The AI button ignored the saved prompt");
                Require(button.ToolTip as string == "Cancel AI Generation" && !((Button)owner.FindName("CommitSelectedButton")).IsEnabled, "Busy state did not appear");
                draft.Text = "user edit while generating";
                pending.SetResult(Reply("AIDF-100 feat describe selected changes"));
                // The result must be reviewed rather than silently overwriting the new draft.
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.Title == "AI Commit Message");
                    if (dialog == null) return;
                    dialog.Close(); timer.Stop();
                };
                timer.Start(); Wait(generation); timer.Stop();
                Require(draft.Text == "user edit while generating", "Generation overwrote an edited draft");
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                generation = Generate(); Until(() => calls == 2, pump); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(generation);
                Require(draft.Text == "user edit while generating" && button.ToolTip as string == "Generate Commit Message with AI", "Cancel changed the draft or left busy state");
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                generation = Generate(); Until(() => calls == 3, pump); pending.SetResult(Reply("AIDF-100 feat use the selected diff")); Wait(generation);
                Require(draft.Text == "AIDF-100 feat use the selected diff", "AI message was not inserted");
                Require(((TextBlock)owner.FindName("StatusLabel")).Text.Contains("1234 input / 48 output tokens"), "Actual provider usage did not appear");
                Require((WaitResult(git.StatusAsync(), Wait)).Changes.Single().IsUntracked, "Generation changed the Git index");
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                generation = Generate(); Until(() => calls == 4, pump); choices["README.md"].Included = false; pending.SetResult(Reply("stale result")); Wait(generation);
                Require(draft.Text == "AIDF-100 feat use the selected diff", "Changed selection accepted a stale result");
                choices["README.md"].Included = true;
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                generation = Generate(); Until(() => calls == 5, pump); File.AppendAllText(Path.Combine(root, "README.md"), "external modification\n"); pending.SetResult(Reply("stale file result")); Wait(generation);
                Require(draft.Text == "AIDF-100 feat use the selected diff", "Changed file accepted a stale result");
            }
            finally
            {
                credentials.Save(AiProvider.OpenAI, ""); credentials.Save(AiProvider.DeepSeek, ""); owner.Close(); previous.Save();
                // The directory is an isolated fixture created above, never a user project.
                if (Directory.Exists(root)) { foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(root, true); }
            }
        });
    }

    private static T WaitResult<T>(Task<T> task, Action<Task> wait) { wait(task); return task.Result; }
    private static void Until(Func<bool> condition, Action pump)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timer.Elapsed.TotalSeconds < 20) { pump(); Thread.Sleep(10); }
        Require(condition(), "AI request did not start");
    }
    private static HttpResponseMessage Reply(string message) => new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = message }, finish_reason = "stop" } }, usage = new { prompt_tokens = 1234, completion_tokens = 48 } }), Encoding.UTF8, "application/json") };
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}

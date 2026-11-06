using System.Net;
using System.Text;
using System.Text.Json;
using IdeaLite.Core;

internal static class AiCommitChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, Func<string, Task<GitService>> repository)
    {
        await test("AI prompt editing, defaults, ticket detection and variable expansion match actual requests", async () =>
        {
            var context = new CommitMessageContext("feature-AIDF-2468", "+unrelated AIDF-9999 in a document", "", false);
            Require(AiCommitService.DetectTaskId(context) == "AIDF-2468");
            Require(AiCommitService.BuildPrompt(context).Contains("exactly 'AIDF-2468 '"));
            Require(AiCommitService.DetectTaskId(context with { Draft = "AIGC-30 fix draft" }) == "AIGC-30");
            var noTicket = context with { Branch = "main" };
            Require(AiCommitService.DetectTaskId(noTicket) == "" && AiCommitService.BuildPrompt(noTicket).Contains("Do not invent a task number"));
            Require(AiCommitService.BuildPrompt(context, template: " \n ") == AiCommitService.BuildPrompt(context));
            await Throws<InvalidOperationException>(() => Task.FromResult(AiCommitService.BuildPrompt(context, template: new string('x', AiCommitService.MaxPromptCharacters + 1))));
            const string template = "Only describe the change in {language}.\nTicket={task_id}\n{task_rule}\n{language_rule}\nLiteral JSON: {\"type\":\"fix\"}";
            var expected = AiCommitService.BuildPrompt(context, "zh-CN", template);
            Require(expected.Contains("Ticket=AIDF-2468") && expected.Contains("Simplified Chinese") && expected.Contains("{\"type\":\"fix\"}"));
            Require(!expected.Contains("{task_rule}") && !expected.Contains("{language_rule}") && !expected.Contains("For an amend"));
            foreach (var provider in Enum.GetValues<AiProvider>())
            {
                using var http = new HttpClient(new Stub(async (request, token) =>
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    Require(body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() == expected);
                    return Reply("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"fix generated message\"},\"finish_reason\":\"stop\"}]}");
                }));
                await new AiCommitService(http).GenerateAsync(provider, "fixture-key", context, "zh-CN", promptTemplate: template);
            }
        });
        await test("AI provider contracts use official endpoints and provider-specific parameters", async () =>
        {
            foreach (var provider in Enum.GetValues<AiProvider>())
            {
                using var http = new HttpClient(new Stub(async (request, token) =>
                {
                    Require(request.RequestUri?.AbsoluteUri == (provider == AiProvider.OpenAI ? "https://api.openai.com/v1/chat/completions" : "https://api.deepseek.com/chat/completions"));
                    Require(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "fixture-key");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    var root = body.RootElement;
                    Require(root.GetProperty("model").GetString() == AiCommitService.DefaultModel(provider));
                    Require(!root.GetProperty("stream").GetBoolean());
                    Require(root.TryGetProperty(provider == AiProvider.OpenAI ? "max_completion_tokens" : "max_tokens", out var budget) && budget.GetInt32() == 256);
                    Require(!root.TryGetProperty(provider == AiProvider.OpenAI ? "max_tokens" : "max_completion_tokens", out _));
                    Require(provider == AiProvider.OpenAI ? !root.TryGetProperty("thinking", out _) : root.GetProperty("thinking").GetProperty("type").GetString() == "disabled");
                    var prompt = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
                    Require(prompt.Contains("AIDF-1234") && prompt.Contains("English") && prompt.Contains("untrusted"));
                    var userContent = root.GetProperty("messages")[1].GetProperty("content").GetString()!;
                    Require(userContent.Contains("中文") && !userContent.Contains("\\u4e2d"));
                    using var context = JsonDocument.Parse(userContent);
                    Require(context.RootElement.GetProperty("Changes").GetString() == "+actual diff 中文");
                    return Reply(JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = " AIDF-1234 fix preserve selected changes " }, finish_reason = "stop" } }, usage = new { prompt_tokens = 1200, completion_tokens = 30 } }));
                }));
                var result = await new AiCommitService(http).GenerateAsync(provider, "fixture-key", new("feature-AIDF-1234", "+actual diff 中文", "existing draft", false));
                Require(result.Message == "AIDF-1234 fix preserve selected changes" && result.InputTokens == 1200 && result.OutputTokens == 30);
            }
        });
        await test("AI custom model, Chinese prompt and task number from draft", async () =>
        {
            using var http = new HttpClient(new Stub(async (request, token) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Require(body.RootElement.GetProperty("model").GetString() == "custom-model");
                var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                Require(prompt.Contains("AIGC-20") && !prompt.Contains("'AIDF-99 '") && prompt.Contains("Simplified Chinese"));
                return Reply(JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = "```text\nAIGC-20 fix 修复提交说明\n```" }, finish_reason = "stop" } } }));
            }));
            var result = await new AiCommitService(http).GenerateAsync(AiProvider.OpenAI, "fixture-key", new("AIDF-99", "diff", "AIGC-20 fix draft", false), "zh-CN", "custom-model");
            Require(result.Message == "AIGC-20 fix 修复提交说明" && result.InputTokens == null && result.OutputTokens == null);
        });
        await test("AI errors never echo server payload or key", async () =>
        {
            foreach (var status in new[] { 400, 401, 402, 403, 404, 429, 500, 302 })
            {
                using var http = new HttpClient(new Stub((_, _) => Task.FromResult(Reply("private-key-and-private-diff", (HttpStatusCode)status))));
                try { await new AiCommitService(http).GenerateAsync(AiProvider.DeepSeek, "private-key-and-private-diff", new("main", "diff", "", false)); throw new Exception("Expected provider failure"); }
                catch (InvalidOperationException error) { Require(!error.ToString().Contains("private-key-and-private-diff")); }
            }
            foreach (var body in new[] { "invalid json private-key-and-private-diff", "{}", "{\"choices\":[]}", "{\"choices\":[{\"message\":{\"content\":null}}]}", "{\"choices\":[{\"message\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}" })
            {
                using var http = new HttpClient(new Stub((_, _) => Task.FromResult(Reply(body))));
                await Throws<InvalidOperationException>(() => new AiCommitService(http).GenerateAsync(AiProvider.OpenAI, "fixture-key", new("main", "diff", "", false)));
            }
        });
        await test("AI cancellation, timeout and transport failure preserve safe error messages", async () =>
        {
            var context = new CommitMessageContext("main", "diff", "", false);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            using var cancelHttp = new HttpClient(new Stub((_, token) => Task.FromCanceled<HttpResponseMessage>(token)));
            await Throws<OperationCanceledException>(() => new AiCommitService(cancelHttp).GenerateAsync(AiProvider.OpenAI, "fixture-key", context, token: canceled.Token));
            using var timeoutHttp = new HttpClient(new Stub((_, _) => throw new TaskCanceledException()));
            await Throws<TimeoutException>(() => new AiCommitService(timeoutHttp).GenerateAsync(AiProvider.OpenAI, "fixture-key", context));
            using var failedHttp = new HttpClient(new Stub((_, _) => throw new HttpRequestException("private-key-and-private-diff")));
            try { await new AiCommitService(failedHttp).GenerateAsync(AiProvider.OpenAI, "fixture-key", context); throw new Exception("Expected transport failure"); }
            catch (InvalidOperationException error) { Require(!error.ToString().Contains("private-key-and-private-diff")); }
            await Throws<InvalidOperationException>(() => new AiCommitService(failedHttp).GenerateAsync(AiProvider.OpenAI, "", context));
        });
        await test("AI diff reflects selected working tree, new files and deletion without touching index", async () =>
        {
            var git = await repository("ai-context");
            foreach (var file in new[] { "selected.txt", "leave.txt", "delete.txt" }) await File.WriteAllTextAsync(Path.Combine(git.Root, file), "base\n");
            await git.StageAsync(["."]); await git.CommitAsync("base");
            await File.WriteAllTextAsync(Path.Combine(git.Root, "selected.txt"), "intermediate staging\n"); await git.StageAsync(["selected.txt"]);
            await File.WriteAllTextAsync(Path.Combine(git.Root, "selected.txt"), "selected working\n");
            await File.WriteAllTextAsync(Path.Combine(git.Root, "leave.txt"), "unrelated staging\n"); await git.StageAsync(["leave.txt"]);
            await File.WriteAllTextAsync(Path.Combine(git.Root, "[新增] 中文.txt"), "new content 中文\n");
            File.Delete(Path.Combine(git.Root, "delete.txt"));
            var before = (await git.RunAsync(["ls-files", "--stage", "-z"])).EnsureSuccess().Output;
            var context = await git.CommitContextAsync(["selected.txt", "[新增] 中文.txt", "delete.txt"]);
            Require(context.Changes.Contains("+selected working") && context.Changes.Contains("new content 中文") && context.Changes.Contains("deleted file"));
            Require(!context.Changes.Contains("intermediate staging") && !context.Changes.Contains("leave.txt") && !context.Changes.Contains("unrelated staging"));
            Require((await git.RunAsync(["ls-files", "--stage", "-z"])).EnsureSuccess().Output == before);
            await Throws<InvalidOperationException>(() => git.CommitContextAsync([]));
            await Throws<InvalidOperationException>(() => git.CommitContextAsync(["missing.txt"]));
        });
        await test("AI first commit, literal paths, rename and binary context", async () =>
        {
            var git = await repository("ai-unborn");
            await File.WriteAllTextAsync(Path.Combine(git.Root, "before.txt"), "first commit content\n"); await git.StageAsync(["before.txt"]);
            var context = await git.CommitContextAsync(["before.txt"]); Require(context.Changes.Contains("first commit content"));
            await Throws<InvalidOperationException>(() => git.CommitContextAsync([], amend: true));
            await git.CommitAsync("base"); (await git.RunAsync(["mv", "before.txt", "[after].txt"])).EnsureSuccess();
            await File.WriteAllBytesAsync(Path.Combine(git.Root, "image.bin"), [0, 1, 2, 0, 4]);
            context = await git.CommitContextAsync(["[after].txt", "image.bin"]);
            Require(context.Changes.Contains("rename from before.txt") && context.Changes.Contains("rename to [after].txt") && context.Changes.Contains("contents omitted"));
        });
        await test("AI amend includes the original commit and selected delta with bounded context", async () =>
        {
            var git = await repository("ai-amend");
            await File.WriteAllTextAsync(Path.Combine(git.Root, "selected.txt"), "original feature\n"); await git.StageAsync(["."]); await git.CommitAsync("feat original feature");
            await File.WriteAllTextAsync(Path.Combine(git.Root, "selected.txt"), "amend fix\n");
            await File.WriteAllTextAsync(Path.Combine(git.Root, "large.txt"), new string('x', 200_000));
            var context = await git.CommitContextAsync(["selected.txt", "large.txt"], amend: true);
            Require(context.Amend && context.Changes.Contains("feat original feature") && context.Changes.Contains("+original feature") && context.Changes.Contains("+amend fix") && context.Changes.Contains("truncated"));
            Require(context.Changes.Length <= 16_000);
            context = await git.CommitContextAsync([], amend: true); Require(context.Changes.Contains("original feature"));
            var files = Enumerable.Range(0, 6).Select(index => "large-new-" + index + ".txt").ToArray();
            foreach (var file in files) await File.WriteAllTextAsync(Path.Combine(git.Root, file), new string('z', 30_000));
            context = await git.CommitContextAsync(files);
            Require(context.Changes.Length <= 16_000 && context.Changes.Contains("Diff truncated"));
            Require(files.All(file => context.Changes.Contains("New file: " + file)), "A large file crowded out another file");
            await Throws<InvalidOperationException>(() => git.CommitContextAsync(Enumerable.Range(0, 101).Select(index => "file-" + index)));
        });
    }

    private static HttpResponseMessage Reply(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private static void Require(bool condition, string message = "AI check failed") { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}

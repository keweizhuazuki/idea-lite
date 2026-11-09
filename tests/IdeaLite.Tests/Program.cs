using System.Text;
using IdeaLite.Core;

if (args.Contains("--wait")) { await Task.Delay(30_000); return 0; }
if (args.Contains("--stream-probe"))
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.Write("first 中文\n"); Console.Out.Flush();
    Console.Error.Write("warning 中文\n"); Console.Error.Flush();
    await Task.Delay(1500);
    Console.Write("last\n"); return 0;
}

var parent = Environment.GetEnvironmentVariable("IDEALITE_TEST_TEMP") ?? (Directory.Exists(@"D:\Codex\work") ? @"D:\Codex\work" : Path.GetTempPath());
var temporary = Path.GetFullPath(Path.Combine(parent, "idea-lite-tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(temporary);
int passed = 0, failed = 0;
Console.WriteLine("Integration fixtures: " + temporary);
void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}]"); }
void True(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
async Task Test(string name, Func<Task> action)
{
    var filter = args.FirstOrDefault(argument => argument.StartsWith("--filter=", StringComparison.Ordinal));
    if (filter != null && !name.Contains(filter[9..], StringComparison.OrdinalIgnoreCase)) return;
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + "\n" + e); }
}
string Folder(string name) { var path = Path.Combine(temporary, name); Directory.CreateDirectory(path); return path; }
async Task<GitService> Repository(string name)
{
    var git = new GitService(Folder(name));
    (await git.RunAsync(["init", "-b", "main"])).EnsureSuccess();
    (await git.RunAsync(["config", "user.name", "Integration Tests"])).EnsureSuccess();
    (await git.RunAsync(["config", "user.email", "tests@example.invalid"])).EnsureSuccess();
    (await git.RunAsync(["config", "core.autocrlf", "false"])).EnsureSuccess();
    (await git.RunAsync(["config", "commit.gpgsign", "false"])).EnsureSuccess();
    return git;
}

await Test("EditorConfig globs: *, **/, braces and case", () =>
{
    True(EditorStyle.GlobMatches("*.{java,kt}", "Hello.java"));
    True(EditorStyle.GlobMatches("src/**/*.java", "src/Hello.java"));
    True(EditorStyle.GlobMatches("src/**/*.java", "src/a/b/Hello.java"));
    True(!EditorStyle.GlobMatches("*.java", "Hello.xml"));
    return Task.CompletedTask;
});

await Test("Java Javadoc stars do not become one-space code indentation", async () =>
{
    var root = Folder("java-indent");
    var path = Path.Combine(root, "Example.java");
    const string source = "/**\n * Class docs.\n */\nclass Example {\n    /**\n     * Method docs.\n     */\n    int value() {\n        return 1;\n    }\n}\n";
    Equal(4, EditorStyle.Load(path, source).IndentSize);
    Equal(1, EditorStyle.Load(path, "class One {\n int value;\n}\n").IndentSize);
    await File.WriteAllTextAsync(Path.Combine(root, ".editorconfig"), "root=true\n[*.java]\nindent_size=2\n");
    Equal(2, EditorStyle.Load(path, source).IndentSize);
});

await Test("Nested EditorConfig and whitespace policy", async () =>
{
    var folder = Folder("styles");
    await File.WriteAllTextAsync(Path.Combine(folder, ".editorconfig"), "root=true\n[*]\nindent_style=space\nindent_size=4\n[*.{java,kt}]\nindent_size=2\nend_of_line=lf\ninsert_final_newline=true\ntrim_trailing_whitespace=true\n");
    var path = Path.Combine(folder, "Hello.java"); await File.WriteAllTextAsync(path, "class Hello {\r\n    int x;  \r\n}");
    var file = await FileService.ReadAsync(path);
    Equal(2, file.Style.IndentSize); Equal("\n", file.Style.NewLine);
    var saved = await FileService.SaveAsync(file, file.Text);
    Equal("class Hello {\n    int x;\n}\n", saved.Text);
    var child = Path.Combine(folder, "nested"); Directory.CreateDirectory(child);
    await File.WriteAllTextAsync(Path.Combine(child, ".editorconfig"), "[*.java]\nindent_style=tab\nindent_size=8\n");
    await File.WriteAllTextAsync(Path.Combine(child, "Child.java"), "class Child {}\n");
    var style = (await FileService.ReadAsync(Path.Combine(child, "Child.java"))).Style;
    True(style.UseTabs); Equal(8, style.IndentSize);
});

await Test("UTF-8 BOM, UTF-16 and GBK survive save", async () =>
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), Encoding.GetEncoding(936) })
    {
        var path = Path.Combine(Folder("encodings"), "e" + encoding.CodePage + ".java");
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("// 中文\r\nclass Demo {}\r\n")).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var file = await FileService.ReadAsync(path); Equal(encoding.CodePage, file.Encoding.CodePage);
        await FileService.SaveAsync(file, file.Text);
        var savedBytes = await File.ReadAllBytesAsync(path);
        True(bytes.SequenceEqual(savedBytes), "Encoding or line endings changed");
    }
});

await Test("External modifications are protected", async () =>
{
    var path = Path.Combine(Folder("external"), "a.txt"); await File.WriteAllTextAsync(path, "first");
    var file = await FileService.ReadAsync(path); await File.WriteAllTextAsync(path, "external");
    await Throws<FileChangedException>(() => FileService.SaveAsync(file, "mine"));
    Equal("external", await File.ReadAllTextAsync(path));
    await FileService.SaveAsync(file, "mine", true); Equal("mine", await File.ReadAllTextAsync(path));
    file = await FileService.ReadAsync(path);
    await File.WriteAllBytesAsync(path, new byte[FileService.MaxFileBytes + 1]);
    await Throws<FileChangedException>(() => FileService.SaveAsync(file, "mine"));
});

await Test("Traversal, oversized and binary inputs are refused", async () =>
{
    var root = Folder("limits");
    await Throws<UnauthorizedAccessException>(() => Task.FromResult(FileService.SafePath(root, "../escape.txt")));
    var path = Path.Combine(root, "huge.txt"); await File.WriteAllBytesAsync(path, new byte[FileService.MaxFileBytes + 1]);
    await Throws<IOException>(() => FileService.ReadAsync(path));
    path = Path.Combine(root, "binary.bin"); await File.WriteAllBytesAsync(path, [1, 0, 3]);
    await Throws<IOException>(() => FileService.ReadAsync(path));
});

await Test("Multiple diff3 conflicts resolve without losing context", () =>
{
    var text = "before\r\n<<<<<<< HEAD\r\nours\r\n||||||| base\r\nbase\r\n=======\r\ntheirs\r\n>>>>>>> topic\r\nafter\r\n<<<<<<< HEAD\r\nx\r\n=======\r\ny\r\n>>>>>>> topic";
    var blocks = ConflictParser.Parse(text); Equal(2, blocks.Count); Equal("base\r\n", blocks[0].Base);
    text = ConflictParser.Resolve(text, blocks[0], "both"); Equal(1, ConflictParser.Parse(text).Count);
    text = ConflictParser.Resolve(text, ConflictParser.Parse(text)[0], "theirs");
    Equal("before\r\nours\r\ntheirs\r\nafter\r\ny\r\n", text);
    Equal(0, ConflictParser.Parse(text).Count);
    return Task.CompletedTask;
});

await Test("Malformed conflicts are never silently marked resolved", async () =>
{
    await Throws<FormatException>(() => Task.FromResult(ConflictParser.Parse("<<<<<<< HEAD\na\n")));
    Equal(0, ConflictParser.Parse("x <<<<<<< text\n").Count);
});

await Test("Maven namespaces, properties and dependency management", async () =>
{
    var root = Folder("maven"); var path = Path.Combine(root, "pom.xml");
    await File.WriteAllTextAsync(path, """
        <project xmlns="http://maven.apache.org/POM/4.0.0"><modelVersion>4.0.0</modelVersion>
          <groupId>example</groupId><artifactId>shop</artifactId><version>1.0</version>
          <properties><spring.version>3.5.0</spring.version></properties><modules><module>api</module></modules>
          <dependencyManagement><dependencies><dependency><groupId>org.springframework</groupId><artifactId>spring-core</artifactId><version>${spring.version}</version></dependency></dependencies></dependencyManagement>
          <dependencies><dependency><groupId>org.springframework</groupId><artifactId>spring-core</artifactId></dependency></dependencies>
        </project>
        """);
    var module = MavenAnalyzer.Parse(path, "pom.xml"); Equal("shop", module.Artifact); Equal("api", module.Modules[0]); Equal("3.5.0", module.Dependencies[0].Version);
    await File.WriteAllTextAsync(path, "<!DOCTYPE x [<!ENTITY xxe SYSTEM 'file:///secret'>]><project>&xxe;</project>");
    await Throws<System.Xml.XmlException>(() => Task.FromResult(MavenAnalyzer.Parse(path, "pom.xml")));
});

await Test("Java types/methods and Spring routes are searchable", () =>
{
    var symbols = new List<SearchHit>(); var spring = new List<SearchHit>();
    JavaAnalyzer.Analyze("src/OrderController.java", """
        package demo.shop;
        @RestController
        @RequestMapping("/orders")
        public class OrderController {
            @GetMapping("/{id}")
            public Order findById(Long id) { return null; }
        }
        """, symbols, spring);
    True(symbols.Any(s => s.Name == "OrderController" && s.Detail == "demo.shop.OrderController"));
    True(symbols.Any(s => s.Name == "findById" && s.Line == 6));
    True(spring.Any(s => s.Name == "GET /orders/{id}"));
    var snapshot = new WorkspaceSnapshot("x", ["src/OrderController.java"], symbols, spring, [], false);
    True(WorkspaceService.Search(snapshot, "OC").Count > 0);
    True(WorkspaceService.Search(snapshot, "", "files").All(hit => hit.Kind == "文件"));
    Equal("OrderController", WorkspaceService.Search(snapshot, "", "classes").Single().Name);
    True(WorkspaceService.Search(snapshot, "findById", "symbols").Any(hit => hit.Kind == "方法"));
    Equal(1, WorkspaceService.FindDefinitions(snapshot, "findById", "src/OrderController.java", "package demo.shop;").Count);
    return Task.CompletedTask;
});

await Test("Java class usages resolve source packages and ignore imports, declarations and literals", async () =>
{
    var git = await Repository("java-usages");
    await File.WriteAllTextAsync(Path.Combine(git.Root, "Order.java"), """
        package shop;
        public class Order {
            public Order() {}
        }
        """);
    await File.WriteAllTextAsync(Path.Combine(git.Root, "OtherOrder.java"), "package other;\nclass Order {}\n");
    await File.WriteAllTextAsync(Path.Combine(git.Root, "Buyer.java"), """
        package client;
        import shop.Order;
        class Buyer {
            Order order = new Order();
            other.Order unrelated;
            String literal = "Order";
            // Order commented;
        }
        """);
    await File.WriteAllTextAsync(Path.Combine(git.Root, "Wildcard.java"), "package client;\nimport shop.*;\nclass Wildcard { Order order; }\n");
    await File.WriteAllTextAsync(Path.Combine(git.Root, "SamePackage.java"), "package shop;\nclass SamePackage { Order order; }\n");
    await File.WriteAllTextAsync(Path.Combine(git.Root, "External.java"), "package client;\nimport external.Order;\nclass External { Order order; }\n");
    var snapshot = await WorkspaceService.LoadAsync(git.Root, git, default);
    var orders = snapshot.Symbols.Where(s => s.Name == "Order" && s.Kind == "类型").ToDictionary(s => s.Detail);
    var references = snapshot.JavaTypeUsages[orders["shop.Order"]];
    Equal(4, references.Count);
    True(references.Where(reference => reference.Path == "Buyer.java").SequenceEqual(
        [new JavaTypeReference("Buyer.java", 4, 5), new JavaTypeReference("Buyer.java", 4, 23)]),
        "Multiple uses on one line should retain their own columns");
    Equal(new JavaTypeReference("Buyer.java", 5, 11), snapshot.JavaTypeUsages[orders["other.Order"]].Single());
});

await Test("Workspace respects Git ignores, content regex and cancellation", async () =>
{
    var git = await Repository("workspace");
    await File.WriteAllTextAsync(Path.Combine(git.Root, ".gitignore"), "secrets/\n");
    Directory.CreateDirectory(Path.Combine(git.Root, "secrets"));
    await File.WriteAllTextAsync(Path.Combine(git.Root, "secrets", "Private.java"), "class Private {}");
    await File.WriteAllTextAsync(Path.Combine(git.Root, "Public.java"), "public class Public {\n    public void hello() {}\n}\n");
    var snapshot = await WorkspaceService.LoadAsync(git.Root, git, default);
    True(!snapshot.Files.Any(p => p.Contains("Private"))); True(snapshot.Symbols.Any(s => s.Name == "Public"));
    var hits = await WorkspaceService.SearchTextAsync(snapshot, "void\\s+hello", false, true, default); Equal(1, hits.Count); Equal(2, hits[0].Line);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await Throws<OperationCanceledException>(() => WorkspaceService.SearchTextAsync(snapshot, "hello", false, false, cancelled.Token));
});

await Test("Search refresh discovers external untracked files and preserves Git ignore boundaries", async () =>
{
    var git = await Repository("search-refresh");
    await File.WriteAllTextAsync(Path.Combine(git.Root, ".gitignore"), "private/\n");
    var snapshot = await WorkspaceService.LoadAsync(git.Root, git, default);
    True(ReferenceEquals(snapshot, await WorkspaceService.RefreshFilesAsync(snapshot, git, default)));
    Directory.CreateDirectory(Path.Combine(git.Root, "private"));
    await File.WriteAllTextAsync(Path.Combine(git.Root, "private", "Private.java"), "class Private {}\n");
    await File.WriteAllTextAsync(Path.Combine(git.Root, "FreshLink.java"), "public class FreshLink {}\n");
    var fresh = await WorkspaceService.RefreshFilesAsync(snapshot, git, default);
    True(WorkspaceService.Search(fresh, "FreshLink", "files").Any(hit => hit.Path == "FreshLink.java"));
    True(WorkspaceService.Search(fresh, "FreshLink", "classes").Any(hit => hit.Name == "FreshLink"));
    True(!fresh.Files.Any(path => path.StartsWith("private/")));
    File.Delete(Path.Combine(git.Root, "FreshLink.java"));
    True(!WorkspaceService.Search(await WorkspaceService.RefreshFilesAsync(fresh, git, default), "FreshLink").Any());
});

await Test("HEAD baseline distinguishes an unborn branch and excludes staged changes", async () =>
{
    var git = await Repository("head-baseline");
    Equal("", await git.HeadTextAsync("Example.java"));
    const string source = "class Example {}\n";
    await File.WriteAllTextAsync(Path.Combine(git.Root, "Example.java"), source); await git.StageAsync(["Example.java"]);
    (await git.RunAsync(["commit", "-m", "fixture subject", "-m", "A full commit body.\nSecond body line."])).EnsureSuccess();
    await File.WriteAllTextAsync(Path.Combine(git.Root, "Example.java"), "class Changed {}\n"); await git.StageAsync(["Example.java"]);
    Equal(source, await git.HeadTextAsync("Example.java"));
});

await Test("Java index distinguishes comments, strings and fields", () =>
{
    var symbols = new List<SearchHit>(); var spring = new List<SearchHit>();
    JavaAnalyzer.Analyze("Web.java", """
        package demo;
        // public class Fake {}
        @RestController
        public class Web {
            private final String address = "http://example.test";
            /* public void fake() {} */
            @GetMapping("/hello")
            public String hello() { return address; }
        }
        """, symbols, spring);
    True(symbols.Any(s => s.Name == "address" && s.Kind == "字段"));
    True(symbols.Any(s => s.Name == "hello" && s.Line == 8));
    True(!symbols.Any(s => s.Name is "Fake" or "fake"));
    True(spring.Any(s => s.Name == "GET /hello"));
    return Task.CompletedTask;
});

await Test("Porcelain NUL parser handles rename, unicode and unusual paths", () =>
{
    var status = GitService.ParseStatus("## main...origin/main [ahead 1]\0R  新 文件.java\0old name.java\0?? -option.java\0UU conflict.txt\0");
    Equal(3, status.Changes.Count); Equal("old name.java", status.Changes[0].OriginalPath); Equal("新 文件.java", status.Changes[0].Path);
    True(status.Changes[2].IsConflict); True(status.Changes[0].IsStaged);
    return Task.CompletedTask;
});

await Test("Git root uses a native absolute path for Windows shell dialogs", async () =>
{
    var git = await Repository("路径 with spaces");
    var root = await GitService.FindRootAsync(git.Root);
    Equal(Path.GetFullPath(git.Root), root);
    True(Directory.Exists(root));
    if (OperatingSystem.IsWindows()) True(!root!.Contains('/'), "Git-style slashes reached the Windows shell");
});

await Test("Git discovery distinguishes ordinary directories from broken repositories", async () =>
{
    Equal<string?>(null, await GitService.FindRootAsync(Folder("ordinary-folder")));
    var git = await Repository("broken-git");
    await File.WriteAllTextAsync(Path.Combine(git.Root, ".git", "config"), "[invalid config");
    await Throws<InvalidOperationException>(() => GitService.FindRootAsync(git.Root));
});

if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe")))
await Test("Installed Git is found when Explorer PATH does not contain Git", async () =>
{
    var git = await Repository("explorer-path");
    var previous = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("PATH", Environment.SystemDirectory);
        True(Path.IsPathFullyQualified(GitService.ExecutablePath), "Git installation was not discovered");
        Equal(git.Root, await GitService.FindRootAsync(git.Root));
        Equal("main", (await git.StatusAsync()).Branch.Split(' ').Last());
    }
    finally { Environment.SetEnvironmentVariable("PATH", previous); }
});

if (args.Contains("--with-wsl"))
{
    await Test("WSL paths and quoted arguments preserve Unicode and shell metacharacters", async () =>
    {
        var text = "中文 'quoted' $not_a_variable `literal`";
        var result = await WslProcess.RunAsync("printf '%s' " + WslProcess.Quote(text), temporary);
        True(result.ExitCode == 0, result.Combined); Equal(text, result.Output);
    });
    await Test("Stopping a WSL build terminates its Linux process group", async () =>
    {
        using var cancellation = new CancellationTokenSource();
        var output = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = WslProcess.RunAsync("echo $$; sleep 30; echo should-not-run", temporary, cancellation.Token, chunk => output.TrySetResult(chunk));
        var pid = (await output.Task.WaitAsync(TimeSpan.FromSeconds(15))).Trim();
        True(int.TryParse(pid, out _), "Missing Linux process id: " + pid);
        cancellation.Cancel();
        await Throws<OperationCanceledException>(() => running);
        var check = await WslProcess.RunAsync("if kill -0 -- -" + pid + " 2>/dev/null; then exit 1; fi", temporary);
        Equal(0, check.ExitCode);
    });
    await Test("WSL build timeout reports timeout instead of success", async () =>
    {
        await Throws<TimeoutException>(() => WslProcess.RunAsync("sleep 30", temporary, timeoutSeconds: 1));
    });
}

await Test("Unborn repo: stage, diff and unstage leave working file intact", async () =>
{
    var git = await Repository("unborn"); var name = "-中文 file.java";
    await File.WriteAllTextAsync(Path.Combine(git.Root, name), "class First {}\n");
    var untracked = (await git.StatusAsync()).Changes.Single(); True(untracked.IsUntracked);
    await git.StageAsync([name]); var staged = (await git.StatusAsync()).Changes.Single(); True(staged.IsStaged);
    var diff = await git.DiffAsync(staged, true); Equal("", diff.OldText); Equal("class First {}\n", diff.NewText);
    await File.WriteAllTextAsync(Path.Combine(git.Root, name), "class Changed {}\n");
    await git.UnstageAsync([name]); True(File.Exists(Path.Combine(git.Root, name))); True((await git.StatusAsync()).Changes.Single().IsUntracked);
    Equal("class Changed {}\n", await File.ReadAllTextAsync(Path.Combine(git.Root, name)));
});

await Test("Staged and unstaged diffs use the correct three Git versions", async () =>
{
    var git = await Repository("versions"); var path = Path.Combine(git.Root, "demo.txt");
    await File.WriteAllTextAsync(path, "HEAD\n"); await git.StageAsync(["demo.txt"]);
    await git.CommitAsync("first 中文\n\nA multi-line message with 'quotes' and $(literal)");
    await File.WriteAllTextAsync(path, "INDEX\n"); await git.StageAsync(["demo.txt"]); await File.WriteAllTextAsync(path, "WORKTREE\n");
    var change = (await git.StatusAsync()).Changes.Single(); True(change.IsStaged && change.HasWorkingChanges);
    var staged = await git.DiffAsync(change, true); Equal("HEAD\n", staged.OldText); Equal("INDEX\n", staged.NewText);
    var working = await git.DiffAsync(change, false); Equal("INDEX\n", working.OldText); Equal("WORKTREE\n", working.NewText);
    await git.UnstageAsync([change.Path]); True(!(await git.StatusAsync()).Changes.Single().IsStaged);
    var message = (await git.RunAsync(["log", "-1", "--format=%B"])).Output; True(message.Contains("$(literal)"));
});

await Test("Real rename and delete diffs preserve original names", async () =>
{
    var git = await Repository("renames"); await File.WriteAllTextAsync(Path.Combine(git.Root, "old 中文.txt"), "original\n");
    await git.StageAsync(["."]); await git.CommitAsync("base");
    (await git.RunAsync(["mv", "--", "old 中文.txt", "new 中文.txt"])).EnsureSuccess();
    var renamed = (await git.StatusAsync()).Changes.Single(); Equal("old 中文.txt", renamed.OriginalPath);
    Equal("original\n", (await git.DiffAsync(renamed, true)).OldText);
    File.Delete(Path.Combine(git.Root, "new 中文.txt"));
    Equal("", (await git.DiffAsync((await git.StatusAsync()).Changes.Single(), false)).NewText);
});

await Test("Real merge conflict: stages 2/3, resolve and commit", async () =>
{
    var git = await Repository("merge"); var path = Path.Combine(git.Root, "conflict.txt");
    await File.WriteAllTextAsync(path, "base\n"); await git.StageAsync(["."]); await git.CommitAsync("base");
    (await git.RunAsync(["switch", "-c", "feature"])).EnsureSuccess();
    await File.WriteAllTextAsync(path, "theirs\n"); await git.StageAsync(["."]); await git.CommitAsync("feature");
    (await git.RunAsync(["switch", "main"])).EnsureSuccess();
    await File.WriteAllTextAsync(path, "ours\n"); await git.StageAsync(["."]); await git.CommitAsync("main");
    Equal(1, (await git.RunAsync(["merge", "feature"])).ExitCode);
    True((await git.StatusAsync()).Changes.Single().IsConflict);
    var sides = await git.ConflictSidesAsync("conflict.txt"); Equal("ours\n", sides.OldText); Equal("theirs\n", sides.NewText);
    var file = await FileService.ReadAsync(path); var blocks = ConflictParser.Parse(file.Text); Equal(1, blocks.Count);
    await FileService.SaveAsync(file, ConflictParser.Resolve(file.Text, blocks[0], "both")); await git.StageAsync(["conflict.txt"]); await git.CommitAsync("resolve");
    Equal(0, (await git.StatusAsync()).Changes.Count); True((await git.LogAsync()).Contains("resolve"));
});

await Test("Stash, local remote push/fetch and fast-forward pull", async () =>
{
    var git = await Repository("remotes"); var path = Path.Combine(git.Root, "a.txt");
    await File.WriteAllTextAsync(path, "v1\n"); await git.StageAsync(["."]); await git.CommitAsync("v1");
    await File.WriteAllTextAsync(path, "v2\n"); (await git.RunAsync(["stash", "push", "-u", "-m", "test"])).EnsureSuccess();
    Equal(0, (await git.StatusAsync()).Changes.Count); (await git.RunAsync(["stash", "pop"])).EnsureSuccess(); Equal("v2\n", await File.ReadAllTextAsync(path));
    await git.StageAsync(["."]); await git.CommitAsync("v2");
    var bare = Folder("remote.git"); (await ProcessRunner.RunAsync("git", ["init", "--bare", "-b", "main"], bare)).EnsureSuccess();
    (await git.RunAsync(["remote", "add", "origin", bare])).EnsureSuccess(); (await git.RunAsync(["push", "-u", "origin", "main"])).EnsureSuccess();
    (await git.RunAsync(["fetch", "--all"])).EnsureSuccess(); (await git.RunAsync(["pull", "--ff-only"])).EnsureSuccess();
    Equal("origin/main", (await git.ReferencesAsync()).Single(reference => reference.Current).Upstream);
});

await Test("Arguments are passed literally without shell execution", async () =>
{
    var git = await Repository("arguments"); var value = "a $(not-a-command) `quotes` & | > 中文";
    (await git.RunAsync(["config", "test.literal", value])).EnsureSuccess(); Equal(value, (await git.RunAsync(["config", "test.literal"])).Output.TrimEnd());
    var split = ProcessRunner.SplitArguments("commit -m \"message with spaces\" -- 'a b.java'");
    Equal(5, split.Length); Equal("message with spaces", split[2]); Equal("a b.java", split[4]);
});

await Test("Process output arrives before exit from stdout and stderr", async () =>
{
    var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var chunks = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var executable = Environment.ProcessPath!;
    var arguments = executable.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(executable) == "dotnet"
        ? new[] { typeof(CommandResult).Assembly.Location.Replace("IdeaLite.Core.dll", "IdeaLite.Tests.dll"), "--stream-probe" } : new[] { "--stream-probe" };
    var running = ProcessRunner.RunAsync(executable, arguments, temporary, onOutput: chunk => { chunks.Enqueue(chunk); first.TrySetResult(); });
    await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
    True(!running.IsCompleted, "Output was buffered until process exit");
    var result = await running;
    Equal(0, result.ExitCode);
    True(result.Output.Contains("first 中文") && result.Output.Contains("last"));
    True(result.Error.Contains("warning 中文"));
    True(string.Concat(chunks).Contains("warning 中文"), "stderr did not stream");
});

await Test("Child process timeout kills and releases process", async () =>
{
    var executable = Environment.ProcessPath!;
    var arguments = executable.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(executable) == "dotnet"
        ? new[] { typeof(GitService).Assembly.Location.Replace("IdeaLite.Core.dll", "IdeaLite.Tests.dll"), "--wait" } : new[] { "--wait" };
    await Throws<TimeoutException>(() => ProcessRunner.RunAsync(executable, arguments, temporary, timeoutSeconds: 1));
});

await Test("Maven-style PowerShell invocation: Unicode, quoting and missing tools", async () =>
{
    if (!OperatingSystem.IsWindows()) return;
    var git = await Repository("powershell");
    var value = "中文 'quotes' $(not-executed) & | >";
    async Task<CommandResult> Invoke(string executable, string[] arguments) => await ProcessRunner.RunAsync(ProcessRunner.PowerShellExecutable, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", ProcessRunner.PowerShellInvocation(executable, arguments)], git.Root);
    (await Invoke("git", ["config", "test.literal", value])).EnsureSuccess();
    Equal(value, (await Invoke("git", ["config", "test.literal"])).EnsureSuccess().Output.TrimEnd());
    var missing = await Invoke("idea-lite-missing-maven-command.cmd", ["test"]);
    Equal(1, missing.ExitCode); True(missing.Error.Contains("idea-lite-missing-maven-command.cmd"));
});

await Test("Log reviews root, renamed and deleted blobs independently of working changes", async () =>
{
    var git = await Repository("history-files");
    Equal(0, (await git.RevisionsAsync(null)).Count);
    string original = "原始 文件.java", renamed = "重命名.java";
    var content = "class Example {}\n";
    await File.WriteAllTextAsync(Path.Combine(git.Root, original), content);
    await git.StageAsync(["."]); await git.CommitAsync("root");
    (await git.RunAsync(["mv", "--", original, renamed])).EnsureSuccess();
    await git.CommitAsync("rename");
    (await git.RunAsync(["rm", "--", renamed])).EnsureSuccess();
    await git.CommitAsync("delete");
    await File.WriteAllTextAsync(Path.Combine(git.Root, renamed), "untracked working copy\n");
    var revisions = await git.RevisionsAsync("HEAD");
    Equal(3, revisions.Count);
    var remove = (await git.RevisionFilesAsync(revisions[0])).Single();
    var move = (await git.RevisionFilesAsync(revisions[1])).Single();
    var add = (await git.RevisionFilesAsync(revisions[2])).Single();
    Equal('D', remove.Status); Equal('R', move.Status); Equal('A', add.Status);
    Equal(original, move.OldPath); Equal(renamed, move.Path);
    Equal("", (await git.RevisionDiffAsync(revisions[0], remove)).NewText);
    var comparison = await git.RevisionDiffAsync(revisions[1], move);
    Equal(content, comparison.OldText); Equal(content, comparison.NewText);
    Equal("", (await git.RevisionDiffAsync(revisions[2], add)).OldText);
});

await Test("Branch filtering, merge parents and first-parent changes in the Git log", async () =>
{
    var git = await Repository("history-graph");
    async Task CommitFile(string name)
    {
        await File.WriteAllTextAsync(Path.Combine(git.Root, name), name + "\n");
        await git.StageAsync([name]); await git.CommitAsync(name);
    }
    await CommitFile("base");
    (await git.RunAsync(["switch", "-c", "feature"])).EnsureSuccess();
    await CommitFile("feature.txt");
    (await git.RunAsync(["switch", "main"])).EnsureSuccess();
    await CommitFile("main.txt");
    (await git.RunAsync(["merge", "--no-ff", "feature", "-m", "merge topic"])).EnsureSuccess();
    var refs = await git.ReferencesAsync();
    True(refs.Single(r => r.Name == "main").Current);
    True((await git.RecentBranchesAsync()).SequenceEqual(new[] { "main", "feature" }));
    var all = await git.RevisionsAsync(null);
    var merge = all.Single(r => r.Subject == "merge topic");
    Equal(2, merge.Parents.Length);
    Equal("feature.txt", (await git.RevisionFilesAsync(merge)).Single().Path);
    var feature = await git.RevisionsAsync(refs.Single(r => r.Name == "feature").FullName);
    Equal(2, feature.Count); True(feature.All(r => r.Subject != "main.txt"));
});

await Test("Amend replaces the selected branch tip and retains the staged tree", async () =>
{
    var git = await Repository("amend");
    await File.WriteAllTextAsync(Path.Combine(git.Root, "first.txt"), "first");
    await git.StageAsync(["."]); await git.CommitAsync("first");
    var previous = (await git.RevisionsAsync("HEAD")).Single();
    await File.WriteAllTextAsync(Path.Combine(git.Root, "next.txt"), "next");
    await git.StageAsync(["next.txt"]); await git.CommitAsync("amended", true);
    var latest = (await git.RevisionsAsync("HEAD")).Single();
    True(latest.Hash != previous.Hash); Equal("amended", latest.Subject);
    Equal(2, (await git.RevisionFilesAsync(latest)).Count);
});

await Test("Checked-file commit excludes unrelated staging and includes selected new files", async () =>
{
    var git = await Repository("checked-commit");
    foreach (var name in new[] { "selected.txt", "leave-staged.txt", "leave-working.txt" })
        await File.WriteAllTextAsync(Path.Combine(git.Root, name), "base");
    await git.StageAsync(["."]); await git.CommitAsync("base");
    await File.WriteAllTextAsync(Path.Combine(git.Root, "leave-staged.txt"), "keep staged");
    await git.StageAsync(["leave-staged.txt"]);
    await File.WriteAllTextAsync(Path.Combine(git.Root, "leave-working.txt"), "keep working");
    await File.WriteAllTextAsync(Path.Combine(git.Root, "selected.txt"), "chosen working content");
    await File.WriteAllTextAsync(Path.Combine(git.Root, "[new] 中文.txt"), "chosen new content");
    await git.CommitSelectedAsync(["selected.txt", "[new] 中文.txt"], "selected only");
    var latest = (await git.RevisionsAsync("HEAD"))[0];
    var committed = (await git.RevisionFilesAsync(latest)).Select(file => file.Path).Order().ToArray();
    True(committed.SequenceEqual(new[] { "[new] 中文.txt", "selected.txt" }.Order()));
    var remaining = (await git.StatusAsync()).Changes;
    True(remaining.Single(c => c.Path == "leave-staged.txt").IsStaged);
    True(remaining.Single(c => c.Path == "leave-working.txt").HasWorkingChanges);
    Equal("keep staged", (await git.RunAsync(["show", ":leave-staged.txt"])).Output);
});

await Test("Checked-file commit handles renames and empty selection without consuming staging", async () =>
{
    var git = await Repository("checked-rename");
    await File.WriteAllTextAsync(Path.Combine(git.Root, "before.txt"), "same content\n");
    await git.StageAsync(["."]); await git.CommitAsync("base");
    (await git.RunAsync(["mv", "--", "before.txt", "after.txt"])).EnsureSuccess();
    await Throws<InvalidOperationException>(() => git.CommitSelectedAsync([], "empty"));
    await git.CommitSelectedAsync(["after.txt"], "rename only");
    var files = await git.RevisionFilesAsync((await git.RevisionsAsync("HEAD"))[0]);
    Equal('R', files.Single().Status);
    Equal("before.txt", files.Single().OldPath);
});

await AiCommitChecks.RunAsync(Test, Repository);

Console.WriteLine($"\n{passed} passed, {failed} failed.");
if (failed == 0 && !args.Contains("--keep"))
{
    var boundary = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!temporary.StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !Path.GetFileName(temporary).StartsWith("idea-lite-tests-"))
        throw new InvalidOperationException("Refusing to clean unexpected fixture path.");
    foreach (var file in Directory.EnumerateFiles(temporary, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(temporary, true);
}
return failed == 0 ? 0 : 1;

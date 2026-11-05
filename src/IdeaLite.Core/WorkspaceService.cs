using System.Text.RegularExpressions;

namespace IdeaLite.Core;

public sealed record SearchHit(string Name, string Path, int Line, string Kind, string Detail)
{
    public string DisplayName => Name + "   · " + Kind;
    public string Location => Kind == "操作" ? Detail : Path + ":" + Line;
    public string Description => Detail == Path ? Kind : Detail;
    public override string ToString() => $"{Name}   {Kind}   {Path}:{Line}  {Detail}";
}
public sealed record WorkspaceSnapshot(string Root, string[] Files, IReadOnlyList<SearchHit> Symbols,
    IReadOnlyList<SearchHit> Spring, IReadOnlyList<MavenModule> Maven, bool Limited);

public static class WorkspaceService
{
    public const int MaxFiles = 50_000;
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".idea", ".vs", ".gradle", ".settings", "node_modules", "target", "build", "bin", "obj", "dist", ".next", ".idea-lite", "vendor", "coverage" };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".java", ".kt", ".kts", ".xml", ".yaml", ".yml", ".properties", ".json", ".md", ".txt", ".sql", ".cs", ".xaml", ".js", ".ts", ".tsx", ".jsx", ".css", ".html", ".vue", ".gradle", ".groovy", ".py", ".go", ".rs", ".sh", ".ps1", ".toml", ".config", ".gitignore", ".editorconfig" };

    public static bool IsSourceFile(string path) => TextExtensions.Contains(Path.GetExtension(path)) || TextExtensions.Contains(Path.GetFileName(path));
    public static bool IsIgnored(string path) => path.Replace('\\', '/').Split('/').Any(Ignored.Contains);

    public static async Task<WorkspaceSnapshot> LoadAsync(string root, GitService? git, CancellationToken token)
    {
        var allFiles = git != null ? await git.FilesAsync(token) : await Task.Run(() => Enumerate(root, token).Take(MaxFiles + 1).ToArray(), token);
        var files = allFiles.Where(p => !IsIgnored(p)).Take(MaxFiles).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return await Task.Run(async () =>
        {
            var symbols = new List<SearchHit>();
            var spring = new List<SearchHit>();
            var maven = new List<MavenModule>();
            long scanned = 0;
            bool limited = allFiles.Length > MaxFiles;
            var pomPaths = files.Where(p => Path.GetFileName(p).Equals("pom.xml", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Length).Take(500).ToArray();
            MavenModule? parent = null;
            foreach (var relative in pomPaths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var module = MavenAnalyzer.Parse(FileService.SafePath(root, relative), relative, parent);
                    maven.Add(module);
                    parent ??= module;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                { maven.Add(new(relative, "解析失败", "", "", [], [], new Dictionary<string, string>(), ex.Message)); }
            }
            foreach (var relative in files)
            {
                token.ThrowIfCancellationRequested();
                if (scanned > 64 * 1024 * 1024 || symbols.Count >= 80_000) { limited = true; break; }
                var extension = Path.GetExtension(relative);
                if (extension is not (".java" or ".kt" or ".properties" or ".yml" or ".yaml")) continue;
                try
                {
                    var path = FileService.SafePath(root, relative);
                    if (!File.Exists(path) || new FileInfo(path).Length > 512 * 1024) continue;
                    var text = await FileService.ReadTextAsync(path, token);
                    scanned += new FileInfo(path).Length;
                    if (extension is ".java" or ".kt") JavaAnalyzer.Analyze(relative, text, symbols, spring);
                    else if (Path.GetFileName(relative).StartsWith("application", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(relative).StartsWith("bootstrap", StringComparison.OrdinalIgnoreCase))
                        spring.Add(new(Path.GetFileName(relative), relative, 1, "配置", "Spring 配置文件"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException) { }
            }
            return new WorkspaceSnapshot(root, files, symbols, spring, maven, limited);
        }, token);
    }

    private static IEnumerable<string> Enumerate(string root, CancellationToken token)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); } catch (IOException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) { if (!Ignored.Contains(Path.GetFileName(entry))) pending.Push(entry); }
                else yield return Path.GetRelativePath(root, entry).Replace('\\', '/');
            }
        }
    }

    public static IReadOnlyList<SearchHit> Search(WorkspaceSnapshot snapshot, string query, string mode = "all")
    {
        var files = snapshot.Files.Select(p => new SearchHit(Path.GetFileName(p), p, 1, "文件", p));
        IEnumerable<SearchHit> candidates = mode switch
        {
            "files" => files,
            "classes" => snapshot.Symbols.Where(s => s.Kind == "类型"),
            "symbols" => snapshot.Symbols,
            _ => files.Concat(snapshot.Symbols).Concat(snapshot.Spring)
        };
        if (query.Length == 0) return candidates.Take(80).ToArray();
        return candidates.Select(h => (hit: h, score: Score(h.Name, query, h.Path)))
            .Where(x => x.score >= 0).OrderByDescending(x => x.score).ThenBy(x => x.hit.Name.Length)
            .DistinctBy(x => (x.hit.Path, x.hit.Line, x.hit.Name)).Take(150).Select(x => x.hit).ToArray();
    }

    private static int Score(string name, string query, string path)
    {
        if (name.Equals(query, StringComparison.Ordinal)) return 1100;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 800;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 600;
        if (path.Contains(query, StringComparison.OrdinalIgnoreCase)) return 300;
        int index = 0;
        foreach (var c in name) if (index < query.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(query[index])) index++;
        return index == query.Length ? 100 : -1;
    }

    public static async Task<IReadOnlyList<SearchHit>> SearchTextAsync(WorkspaceSnapshot snapshot, string query, bool caseSensitive,
        bool regex, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var matcher = new Regex(regex ? query : Regex.Escape(query), caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        return await Task.Run(async () =>
        {
            var hits = new List<SearchHit>();
            foreach (var relative in snapshot.Files.Where(IsSourceFile))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var path = FileService.SafePath(snapshot.Root, relative);
                    if (!File.Exists(path) || new FileInfo(path).Length > 512 * 1024) continue;
                    var text = await FileService.ReadTextAsync(path, token);
                    var lines = text.Replace("\r\n", "\n").Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                        if (matcher.IsMatch(lines[i]))
                        {
                            hits.Add(new(Path.GetFileName(relative), relative, i + 1, "文本", lines[i].Trim()[..Math.Min(lines[i].Trim().Length, 200)]));
                            if (hits.Count >= 250) return hits;
                        }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException) { }
            }
            return (IReadOnlyList<SearchHit>)hits;
        }, token);
    }

    public static IReadOnlyList<SearchHit> FindDefinitions(WorkspaceSnapshot snapshot, string symbol, string currentPath, string currentText)
    {
        var package = Regex.Match(currentText, @"\bpackage\s+([\w.]+)").Groups[1].Value;
        var imports = Regex.Matches(currentText, @"\bimport\s+(?:static\s+)?([\w.]+)\s*;").Select(m => m.Groups[1].Value).ToHashSet();
        return snapshot.Symbols.Where(s => s.Name == symbol).OrderByDescending(s =>
            (s.Path == currentPath ? 4 : 0) + (imports.Contains(s.Detail) ? 8 : 0) + (s.Detail.StartsWith(package + ".", StringComparison.Ordinal) ? 2 : 0)).Take(100).ToArray();
    }
}

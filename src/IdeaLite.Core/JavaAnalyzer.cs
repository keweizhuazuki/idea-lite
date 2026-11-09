using System.Text.RegularExpressions;

namespace IdeaLite.Core;

public static partial class JavaAnalyzer
{
    [GeneratedRegex(@"(?m)^\s*(?:public\s+|private\s+|protected\s+|abstract\s+|final\s+|static\s+|sealed\s+|data\s+)*(?:class|interface|enum|record|object)\s+(\w+)")]
    private static partial Regex Types();
    [GeneratedRegex(@"(?m)^\s*(?:(?:public|private|protected|static|final|abstract|synchronized|default|override|suspend|inline|open)\s+)*(?:(?:<[^>\r\n]+>\s+)?[\w.$<>?,\[\]]+\s+|fun\s+)(\w+)\s*\([^;{}\r\n]*\)\s*(?:throws\s+[\w.,\s]+)?\s*(?:\{|;|=|$)")]
    private static partial Regex Methods();
    [GeneratedRegex(@"(?m)^\s*(?:(?:public|private|protected|static|final|volatile|transient)\s+)+[\w.$<>?,\[\]]+\s+(\w+)\s*(?:=|;)")]
    private static partial Regex Fields();
    [GeneratedRegex("@(RestController|Controller|Service|Repository|Component|Configuration|SpringBootApplication|Bean|FeignClient|Mapper|Entity|ConfigurationProperties|EnableDiscoveryClient|EnableFeignClients|EnableScheduling|Scheduled|Async|Transactional|Cacheable|CacheEvict|PreAuthorize|SecurityFilterChain|KafkaListener|RabbitListener|JmsListener|EventListener|RestControllerAdvice|ExceptionHandler|EnableWebSecurity|EnableCaching|EnableAsync|DubboService|DubboReference|RocketMQMessageListener)\\b(?:\\(([^\\r\\n]*)\\))?")]
    private static partial Regex Beans();
    [GeneratedRegex("@(Get|Post|Put|Delete|Patch|Request)Mapping\\b(?:\\s*\\(([^)]*)\\))?")]
    private static partial Regex Mappings();

    public static void Analyze(string path, string text, List<SearchHit> symbols, List<SearchHit> spring) =>
        AnalyzeSource(path, text, symbols, spring);

    internal static string AnalyzeSource(string path, string text, List<SearchHit> symbols, List<SearchHit> spring)
    {
        var firstSymbol = symbols.Count;
        // Preserve offsets/newlines while masking comments. This is a bounded source index, not a Java compiler.
        var code = Mask(text, false);
        var declarations = Mask(code, true);
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < code.Length; i++) if (code[i] == '\n') lineStarts.Add(i + 1);
        int Line(int offset) { var index = lineStarts.BinarySearch(offset); return index >= 0 ? index + 1 : ~index; }
        var package = Regex.Match(code, @"\bpackage\s+([\w.]+)").Groups[1].Value;
        var types = Types().Matches(declarations).ToArray();
        foreach (var type in types)
        {
            var name = type.Groups[1].Value;
            symbols.Add(new(name, path, Line(type.Groups[1].Index), "类型", string.IsNullOrEmpty(package) ? name : package + "." + name));
        }
        foreach (Match method in Methods().Matches(declarations))
        {
            var declaration = method.Value.TrimStart();
            if (declaration.StartsWith("new ", StringComparison.Ordinal) || declaration.StartsWith("return ", StringComparison.Ordinal) || declaration.StartsWith("throw ", StringComparison.Ordinal)) continue;
            var name = method.Groups[1].Value;
            if (name is "if" or "for" or "while" or "switch" or "catch" or "return" or "new") continue;
            var owner = types.LastOrDefault(t => t.Index <= method.Index)?.Groups[1].Value ?? Path.GetFileNameWithoutExtension(path);
            symbols.Add(new(name, path, Line(method.Groups[1].Index), "方法", package + "." + owner + "." + name));
        }
        foreach (Match field in Fields().Matches(declarations))
        {
            var owner = types.LastOrDefault(t => t.Index <= field.Index)?.Groups[1].Value ?? Path.GetFileNameWithoutExtension(path);
            symbols.Add(new(field.Groups[1].Value, path, Line(field.Groups[1].Index), "字段", package + "." + owner + "." + field.Groups[1].Value));
        }
        var localSymbols = symbols.Skip(firstSymbol).OrderBy(s => s.Line).ToArray();
        foreach (Match bean in Beans().Matches(code))
        {
            var next = localSymbols.FirstOrDefault(s => s.Line >= Line(bean.Index));
            spring.Add(new(next?.Name ?? Path.GetFileNameWithoutExtension(path), path, Line(bean.Index), "@" + bean.Groups[1].Value, bean.Groups[2].Value));
        }
        var firstType = types.FirstOrDefault();
        string prefix = "";
        foreach (Match mapping in Mappings().Matches(code))
        {
            var route = Regex.Match(mapping.Groups[2].Value, "\"([^\"]*)\"").Groups[1].Value;
            if (firstType != null && mapping.Index < firstType.Index) { prefix = route; continue; }
            var verb = mapping.Groups[1].Value == "Request" ? Regex.Match(mapping.Groups[2].Value, @"RequestMethod\.(\w+)").Groups[1].Value : mapping.Groups[1].Value.ToUpperInvariant();
            if (verb.Length == 0) verb = "ANY";
            spring.Add(new(verb + " " + (prefix.TrimEnd('/') + "/" + route.TrimStart('/')).TrimEnd('/'), path, Line(mapping.Index), "端点", Path.GetFileName(path)));
        }
        return declarations;
    }

    [GeneratedRegex(@"\b(package|import)\s+(?:static\s+)?([\w$.]+(?:\.\*)?)\s*;")]
    private static partial Regex Directives();
    [GeneratedRegex(@"[\p{L}\p{Nl}_$][\w$]*")]
    private static partial Regex Identifiers();
    [GeneratedRegex(@"(?m)(?:^|[;{}])\s*(?:(?:public|protected|private)\s+)?([\w$]+)\s*\([^;{}]*\)\s*(?:throws\s+[\w.,\s]+)?\{")]
    private static partial Regex Constructors();

    public static IEnumerable<(string Name, int Offset)> DeclaredTypes(string source) =>
        Types().Matches(Mask(source, true)).Select(match => (match.Groups[1].Value, match.Groups[1].Index));

    public static string? ExplicitTypeImport(string source, string name)
    {
        var suffix = "." + name;
        if (!source.Contains(suffix, StringComparison.Ordinal)) return null;
        return Directives().Matches(Mask(source, true)).Where(match => match.Groups[1].Value == "import")
            .Select(match => match.Groups[2].Value).FirstOrDefault(import => import.EndsWith(suffix, StringComparison.Ordinal));
    }

    internal static IReadOnlyDictionary<SearchHit, IReadOnlyList<JavaTypeReference>> IndexTypeUsages(
        IReadOnlyList<SearchHit> symbols, IEnumerable<(string Path, string Code)> sources, CancellationToken token)
    {
        var byName = symbols.Where(s => s.Kind == "类型" && Path.GetExtension(s.Path) == ".java")
            .GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var names = byName.GetAlternateLookup<ReadOnlySpan<char>>();
        var usages = new Dictionary<SearchHit, List<JavaTypeReference>>();
        foreach (var (path, code) in sources)
        {
            token.ThrowIfCancellationRequested();
            var directives = Directives().Matches(code);
            string package = directives.FirstOrDefault(m => m.Groups[1].Value == "package")?.Groups[2].Value ?? "";
            var imports = directives.Where(m => m.Groups[1].Value == "import").Select(m => m.Groups[2].Value).ToArray();
            var packages = imports.Where(i => i.EndsWith(".*", StringComparison.Ordinal)).Select(i => i[..^1]).ToHashSet(StringComparer.Ordinal);
            var explicitImports = imports.Where(i => !i.EndsWith(".*", StringComparison.Ordinal))
                .ToLookup(i => i[(i.LastIndexOf('.') + 1)..], StringComparer.Ordinal);
            var declarations = Types().Matches(code).Select(m => m.Groups[1]).ToArray();
            var typeNames = declarations.Select(g => g.Value).ToHashSet(StringComparer.Ordinal);
            var excluded = declarations.Select(g => g.Index).ToHashSet();
            foreach (Match constructor in Constructors().Matches(code))
                if (typeNames.Contains(constructor.Groups[1].Value)) excluded.Add(constructor.Groups[1].Index);

            var start = directives.Count > 0 ? directives[^1].Index + directives[^1].Length : 0;
            int line = 1, lineStart = 0;
            foreach (var identifier in Identifiers().EnumerateMatches(code.AsSpan(start)))
            {
                int offset = start + identifier.Index;
                if (excluded.Contains(offset) || !names.TryGetValue(code.AsSpan(offset, identifier.Length), out var candidates)) continue;
                int qualifier = offset;
                while (qualifier > start && (char.IsLetterOrDigit(code[qualifier - 1]) || code[qualifier - 1] is '_' or '$' or '.')) qualifier--;
                SearchHit? target;
                if (qualifier < offset && code[offset - 1] == '.')
                {
                    var qualified = code.AsSpan(qualifier, offset + identifier.Length - qualifier);
                    target = null;
                    foreach (var candidate in candidates)
                    {
                        if (!qualified.SequenceEqual(candidate.Detail)) continue;
                        if (target != null) { target = null; break; }
                        target = candidate;
                    }
                }
                else
                {
                    var name = candidates[0].Name;
                    var explicitNames = explicitImports[name];
                    var samePackage = package.Length == 0 ? name : package + "." + name;
                    int best = 0;
                    target = null;
                    foreach (var candidate in candidates)
                    {
                        int rank = candidate.Path == path ? 4 : explicitNames.Contains(candidate.Detail) ? 3 :
                            explicitNames.Any() ? 0 : candidate.Detail == samePackage ? 2 :
                            packages.Contains(candidate.Detail[..^name.Length]) ? 1 : 0;
                        if (rank > best) { best = rank; target = candidate; }
                        else if (rank > 0 && rank == best) target = null;
                    }
                }
                if (target == null) continue;
                // References arrive in source order, so line and column advance in one pass per file.
                int newline;
                while ((newline = code.IndexOf('\n', lineStart, offset - lineStart)) >= 0)
                {
                    line++;
                    lineStart = newline + 1;
                }
                if (!usages.TryGetValue(target, out var references)) usages.Add(target, references = []);
                references.Add(new(path, line, offset - lineStart + 1));
            }
        }
        return usages.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<JavaTypeReference>)pair.Value);
    }

    private static string Mask(string text, bool maskStrings)
    {
        var result = text.ToCharArray();
        void Blank(int start, int end) { for (int j = start; j < end; j++) if (result[j] is not ('\r' or '\n')) result[j] = ' '; }
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '\"' or '\'')
            {
                var start = i; var quote = text[i];
                var triple = quote == '\"' && i + 2 < text.Length && text[i + 1] == '\"' && text[i + 2] == '\"';
                i += triple ? 3 : 1;
                while (i < text.Length)
                {
                    if (text[i] == '\\') { i += 2; continue; }
                    if (triple && i + 2 < text.Length && text[i] == '\"' && text[i + 1] == '\"' && text[i + 2] == '\"') { i += 3; break; }
                    if (!triple && text[i] == quote) { i++; break; }
                    i++;
                }
                if (maskStrings) Blank(start, Math.Min(i, text.Length));
                i--;
            }
            else if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            {
                var start = i; while (i < text.Length && text[i] != '\n') i++;
                Blank(start, i); i--;
            }
            else if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                var start = i; i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i = Math.Min(text.Length, i + 2); Blank(start, i); i--;
            }
        }
        return new string(result);
    }
}

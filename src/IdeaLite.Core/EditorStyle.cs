using System.Text;
using System.Text.RegularExpressions;

namespace IdeaLite.Core;

public sealed record EditorStyle(bool UseTabs, int IndentSize, string NewLine, bool? FinalNewLine = null,
    bool TrimTrailingWhitespace = false, Encoding? EncodingOverride = null, string Source = "检测到的文件风格")
{
    public static EditorStyle Load(string path, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // Javadoc 的星号通常比代码缩进多一个空格，不能用来推断 Java 缩进宽度。
        var indentationLines = Path.GetExtension(path).Equals(".java", StringComparison.OrdinalIgnoreCase) ? lines.Where(line => !line.TrimStart().StartsWith('*')) : lines;
        var indents = indentationLines.Where(l => l.Length > 0 && l[0] == ' ').Select(l => l.TakeWhile(c => c == ' ').Count()).Where(n => n > 0 && n <= 16).ToArray();
        var tabs = indentationLines.Count(l => l.StartsWith('\t'));
        var size = indents.Length == 0 ? 4 : indents.Min();
        var style = new EditorStyle(tabs > indents.Length, Math.Clamp(size, 1, 8), text.Contains("\r\n") ? "\r\n" : "\n");
        var configs = new List<string>();
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
        while (directory != null)
        {
            var config = Path.Combine(directory.FullName, ".editorconfig");
            if (File.Exists(config) && new FileInfo(config).Length < 128 * 1024)
            {
                configs.Add(config);
                if (File.ReadLines(config).Any(l => Regex.IsMatch(l.Trim(), @"^root\s*=\s*true$", RegexOptions.IgnoreCase))) break;
            }
            directory = directory.Parent;
        }
        configs.Reverse();
        foreach (var config in configs)
        {
            bool active = false;
            foreach (var raw in File.ReadLines(config))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';') continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    var glob = line[1..^1];
                    var relative = Path.GetRelativePath(Path.GetDirectoryName(config)!, path).Replace('\\', '/');
                    active = GlobMatches(glob, glob.Contains('/') ? relative : Path.GetFileName(path));
                    continue;
                }
                var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (!active || pair.Length != 2) continue;
                var value = pair[1].ToLowerInvariant();
                style = pair[0].ToLowerInvariant() switch
                {
                    "indent_style" when value is "tab" or "space" => style with { UseTabs = value == "tab", Source = ".editorconfig" },
                    "indent_size" or "tab_width" when int.TryParse(value, out var n) && n is >= 1 and <= 16 => style with { IndentSize = n, Source = ".editorconfig" },
                    "end_of_line" when value is "crlf" or "lf" or "cr" => style with { NewLine = value == "crlf" ? "\r\n" : value == "cr" ? "\r" : "\n" },
                    "insert_final_newline" when value is "true" or "false" => style with { FinalNewLine = value == "true" },
                    "trim_trailing_whitespace" when value is "true" or "false" => style with { TrimTrailingWhitespace = value == "true" },
                    "charset" => style with
                    {
                        EncodingOverride = value switch
                        {
                            "utf-8" => new UTF8Encoding(false, true),
                            "utf-8-bom" => new UTF8Encoding(true, true),
                            "utf-16le" => new UnicodeEncoding(false, true, true),
                            "utf-16be" => new UnicodeEncoding(true, true, true),
                            "latin1" => Encoding.GetEncoding(28591, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
                            _ => style.EncodingOverride
                        }
                    },
                    _ => style
                };
            }
        }
        return style;
    }

    public static bool GlobMatches(string glob, string path)
    {
        var pattern = new StringBuilder("\\A");
        glob = glob.TrimStart('/');
        for (int i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; pattern.Append("(?:.*/)?"); }
                else pattern.Append(".*");
            }
            else if (glob[i] == '*') pattern.Append("[^/]*");
            else if (glob[i] == '?') pattern.Append("[^/]");
            else if (glob[i] == '{' && glob.IndexOf('}', i) is var end && end > i)
            {
                var alternatives = glob[(i + 1)..end].Split(',');
                pattern.Append("(?:").Append(string.Join('|', alternatives.Select(Regex.Escape))).Append(')'); i = end;
            }
            else pattern.Append(Regex.Escape(glob[i].ToString()));
        }
        pattern.Append("\\z");
        return Regex.IsMatch(path, pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public string Apply(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (TrimTrailingWhitespace) normalized = string.Join('\n', normalized.Split('\n').Select(l => l.TrimEnd(' ', '\t')));
        if (FinalNewLine == true && !normalized.EndsWith('\n')) normalized += "\n";
        if (FinalNewLine == false) normalized = normalized.TrimEnd('\n');
        return normalized.Replace("\n", NewLine);
    }

    public string NormalizeIndentation(string text) => string.Join(NewLine,
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line =>
        {
            var prefix = line.TakeWhile(c => c is ' ' or '\t').ToArray();
            var columns = prefix.Aggregate(0, (n, c) => c == '\t' ? n + IndentSize - n % IndentSize : n + 1);
            return (UseTabs ? new string('\t', columns / IndentSize) + new string(' ', columns % IndentSize) : new string(' ', columns)) + line[prefix.Length..];
        }));
}

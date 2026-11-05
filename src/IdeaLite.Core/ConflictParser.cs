namespace IdeaLite.Core;

public sealed record ConflictBlock(int Start, int Length, string Ours, string Theirs, string? Base);

public static class ConflictParser
{
    public static IReadOnlyList<ConflictBlock> Parse(string text)
    {
        var result = new List<ConflictBlock>();
        int start = -1, oursStart = -1, oursEnd = -1, baseStart = -1, baseEnd = -1, theirsStart = -1;
        int offset = 0;
        foreach (var line in text.Split('\n'))
        {
            var marker = line.TrimEnd('\r');
            var next = Math.Min(text.Length, offset + line.Length + 1);
            if (IsMarker(marker, '<'))
            {
                if (start >= 0) throw new FormatException("存在嵌套冲突标记，请手动检查文件。");
                start = offset; oursStart = next; oursEnd = baseStart = baseEnd = theirsStart = -1;
            }
            else if (start >= 0 && IsMarker(marker, '|')) { oursEnd = offset; baseStart = next; }
            else if (start >= 0 && marker.Length >= 7 && marker.All(c => c == '='))
            {
                if (baseStart >= 0) baseEnd = offset;
                else oursEnd = offset;
                theirsStart = next;
            }
            else if (start >= 0 && IsMarker(marker, '>'))
            {
                if (oursEnd < 0 || theirsStart < 0) throw new FormatException("冲突标记不完整。");
                result.Add(new(start, next - start, text[oursStart..oursEnd], text[theirsStart..offset], baseStart >= 0 && baseEnd >= 0 ? text[baseStart..baseEnd] : null));
                start = -1;
            }
            offset = next;
        }
        if (start >= 0) throw new FormatException("冲突标记不完整。");
        return result;
    }

    private static bool IsMarker(string line, char character)
    {
        int count = line.TakeWhile(c => c == character).Count();
        return count >= 7 && (count == line.Length || line[count] == ' ');
    }

    public static string Resolve(string text, ConflictBlock block, string choice) =>
        text[..block.Start] + (choice switch { "ours" => block.Ours, "theirs" => block.Theirs, "both" => block.Ours + block.Theirs, _ => throw new ArgumentException("未知冲突选择") }) + text[(block.Start + block.Length)..];
}

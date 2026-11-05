using System.Security.Cryptography;
using System.Text;

namespace IdeaLite.Core;

public sealed record OpenedFile(string Path, string Text, Encoding Encoding, string Hash, EditorStyle Style);
public sealed class FileChangedException(string path) : IOException($"文件已被其他程序修改：{path}");

public static class FileService
{
    public const int MaxFileBytes = 4 * 1024 * 1024;
    static FileService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string SafePath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("路径不在当前项目内。");
        var part = Path.GetRelativePath(fullRoot, full);
        var cursor = fullRoot;
        foreach (var segment in part.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            cursor = Path.Combine(cursor, segment);
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("为防止越过项目边界，不通过符号链接或目录联接读写文件。");
        }
        return full;
    }

    public static async Task<OpenedFile> ReadAsync(string path, CancellationToken token = default)
    {
        var data = await ReadDataAsync(path, token);
        return new(path, data.Text, data.Encoding, Convert.ToHexString(SHA256.HashData(data.Bytes)), EditorStyle.Load(path, data.Text));
    }

    public static async Task<string> ReadTextAsync(string path, CancellationToken token = default) => (await ReadDataAsync(path, token)).Text;

    private static async Task<(string Text, Encoding Encoding, byte[] Bytes)> ReadDataAsync(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new IOException("文件超过 4 MiB 编辑限制，请使用外部工具查看。");
        byte[] bytes;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, token)) > 0)
            {
                if (buffer.Length + read > MaxFileBytes) throw new IOException("文件超过 4 MiB 编辑限制。");
                await buffer.WriteAsync(chunk.AsMemory(0, read), token);
            }
            bytes = buffer.ToArray();
        }
        Encoding encoding;
        int offset;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) { encoding = new UTF32Encoding(false, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = new UTF8Encoding(true); offset = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, true); offset = 2; }
        else
        {
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0)) throw new IOException("二进制文件不在文本编辑器中打开。");
            offset = 0;
            try { new UTF8Encoding(false, true).GetString(bytes); encoding = new UTF8Encoding(false, true); }
            catch (DecoderFallbackException) { encoding = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
        }
        var text = encoding.GetString(bytes, offset, bytes.Length - offset);
        return (text, encoding, bytes);
    }

    public static async Task<OpenedFile> SaveAsync(OpenedFile file, string text, bool overwriteExternal = false, CancellationToken token = default)
    {
        if (!overwriteExternal)
        {
            if (!File.Exists(file.Path)) throw new FileChangedException(file.Path);
            if (await HashCurrentFileAsync(file.Path, token) != file.Hash) throw new FileChangedException(file.Path);
        }
        var normalized = file.Style.Apply(text);
        var encoding = file.Style.EncodingOverride ?? file.Encoding;
        var output = encoding.GetPreamble().Concat(encoding.GetBytes(normalized)).ToArray();
        if (output.Length > MaxFileBytes) throw new IOException("保存结果超过 4 MiB 限制。");
        var temporary = file.Path + ".idea-lite-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, output, token);
            if (!overwriteExternal)
            {
                if (!File.Exists(file.Path)) throw new FileChangedException(file.Path);
                if (await HashCurrentFileAsync(file.Path, token) != file.Hash) throw new FileChangedException(file.Path);
            }
            File.Move(temporary, file.Path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return file with { Text = normalized, Encoding = encoding, Hash = Convert.ToHexString(SHA256.HashData(output)) };
    }

    private static async Task<string> HashCurrentFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxFileBytes) throw new FileChangedException(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[8192];
        int length = 0, read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            length += read;
            if (length > MaxFileBytes) throw new FileChangedException(path);
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

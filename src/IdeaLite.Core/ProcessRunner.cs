using System.Diagnostics;
using System.Text;

namespace IdeaLite.Core;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public string Combined => string.IsNullOrWhiteSpace(Error) ? Output : Output + "\n" + Error;
    public CommandResult EnsureSuccess()
    {
        if (ExitCode != 0) throw new InvalidOperationException(Combined.Trim());
        return this;
    }
}

public static class ProcessRunner
{
    public static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments,
        string directory, string? input = null, CancellationToken cancellationToken = default, int timeoutSeconds = 60)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_PAGER"] = "cat";
        info.Environment["GIT_EDITOR"] = "true";
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动 {executable}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var registration = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
        });
        var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            if (input != null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"命令超过 {timeoutSeconds} 秒，已停止。请检查网络、凭据或在终端运行。");
        }
        finally
        {
            // Observe both reader tasks even when a timeout kills the process.
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        const int limit = 8 * 1024 * 1024;
        var result = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            var keep = Math.Min(count, limit - result.Length);
            if (keep > 0) result.Append(buffer, 0, keep);
            if (keep < count) truncated = true;
        }
        if (truncated) throw new InvalidOperationException("命令输出超过 8 MiB 限制。请缩小范围或使用终端。");
        return result.ToString();
    }

    public static string[] SplitArguments(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        bool started = false;
        foreach (var c in text)
        {
            if ((c == '\"' || c == '\'') && (quote == '\0' || quote == c))
            {
                quote = quote == c ? '\0' : c;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && quote == '\0')
            {
                if (started) { result.Add(current.ToString()); current.Clear(); started = false; }
            }
            else { current.Append(c); started = true; }
        }
        if (quote != '\0') throw new ArgumentException("引号没有闭合。");
        if (started) result.Add(current.ToString());
        return result.ToArray();
    }
}

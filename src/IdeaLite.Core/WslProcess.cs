namespace IdeaLite.Core;

/// <summary>
/// 在 WSL 进程组中运行构建，取消时同时停止 Linux 子进程。
/// </summary>
public static class WslProcess
{
    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    public static string LinuxPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length < 3 || full[1] != ':' || !char.IsAsciiLetter(full[0]))
            throw new ArgumentException("WSL 构建需要本地盘符路径，例如 D:\\code\\project。");
        return "/mnt/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
    }

    public static string WindowsBrowsePath(string path, string distributionRoot)
    {
        if (!path.StartsWith('/')) return path;
        if (path.StartsWith("/mnt/", StringComparison.Ordinal) && path.Length >= 6 &&
            char.IsAsciiLetter(path[5]) && (path.Length == 6 || path[6] == '/'))
            return char.ToUpperInvariant(path[5]) + @":\" + path[6..].TrimStart('/').Replace('/', '\\');
        return distributionRoot.TrimEnd('\\') + path.Replace('/', '\\');
    }

    public static string LinuxBrowsePath(string path, string distributionRoot)
    {
        var distribution = distributionRoot.TrimEnd('\\').Split('\\')[^1];
        foreach (var prefix in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
        {
            string root = prefix + distribution;
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return "/";
            if (path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                return path[root.Length..].Replace('\\', '/');
        }
        if (path.Length < 3 || path[1] != ':')
            throw new ArgumentException("请选择本机磁盘或当前 WSL 发行版中的路径。");
        return LinuxPath(path);
    }

    public static async Task<CommandResult> RunAsync(string script, string directory, CancellationToken cancellationToken = default, Action<string>? onOutput = null, int timeoutSeconds = 1200)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        var marker = "/tmp/idea-lite-" + Guid.NewGuid().ToString("N");
        var pid = Quote(marker + ".pid");
        var cancel = Quote(marker + ".cancel");
        var child = $"if [ -e {cancel} ]; then exit 130; fi; echo $$ > {pid}; exec bash -lc {Quote(script)}";
        var wrapper = $"trap {Quote($"rm -f -- {pid} {cancel}")} EXIT; setsid bash -c {Quote(child)} & task=$!; wait \"$task\"";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        Task stop = Task.CompletedTask;
        var registration = deadline.Token.Register(() => stop = StopAsync());
        try
        {
            var result = await ProcessRunner.RunAsync(wsl, ["--cd", LinuxPath(directory), "--exec", "bash", "-lc", wrapper], directory,
                timeoutSeconds: timeoutSeconds + 15, onOutput: onOutput);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"WSL 构建超过 {timeoutSeconds} 秒，已停止。");
        }
        finally
        {
            registration.Dispose();
            await stop;
        }

        async Task StopAsync()
        {
            // The marker also covers cancellation before the Linux group has finished starting.
            var terminate = $"touch {cancel}; for attempt in {{1..30}}; do if [ -s {pid} ]; then read -r group < {pid}; kill -TERM -- \"-$group\" 2>/dev/null || true; break; fi; sleep .1; done";
            (await ProcessRunner.RunAsync(wsl, ["--exec", "bash", "-lc", terminate], directory, timeoutSeconds: 15)).EnsureSuccess();
        }
    }
}

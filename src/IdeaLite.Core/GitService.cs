namespace IdeaLite.Core;

public sealed record GitChange(string Path, string? OriginalPath, char Index, char WorkTree)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";
    public bool IsConflict => Index == 'U' || WorkTree == 'U' || (Index == 'A' && WorkTree == 'A') || (Index == 'D' && WorkTree == 'D');
    public bool IsUntracked => Index == '?';
    public bool IsStaged => Index is not (' ' or '?') && !IsConflict;
    public bool HasWorkingChanges => WorkTree != ' ' || IsUntracked;
    public string Label => IsConflict ? "冲突" : IsUntracked ? "新增" : $"{Index}{WorkTree}";
    public override string ToString() => $"{Label}  {Path}";
}

public sealed record GitStatus(string Branch, IReadOnlyList<GitChange> Changes);
public sealed record GitDiff(string OldText, string NewText, string OldLabel, string NewLabel);

public sealed partial class GitService(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public Task<CommandResult> RunAsync(IEnumerable<string> args, CancellationToken token = default, string? input = null) =>
        ProcessRunner.RunAsync("git", new[] { "--no-pager", "-c", "core.quotepath=false", "-c", "color.ui=false" }.Concat(args), Root, input, token, 120);

    public static async Task<string?> FindRootAsync(string path, CancellationToken token = default)
    {
        var result = await ProcessRunner.RunAsync("git", ["rev-parse", "--show-toplevel"], path, cancellationToken: token);
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    public async Task<GitStatus> StatusAsync(CancellationToken token = default) =>
        ParseStatus((await RunAsync(["status", "--porcelain=v1", "-z", "--branch", "--untracked-files=all"], token)).EnsureSuccess().Output);

    public static GitStatus ParseStatus(string output)
    {
        var records = output.Split('\0');
        var changes = new List<GitChange>();
        var branch = "HEAD";
        for (int i = 0; i < records.Length; i++)
        {
            var line = records[i];
            if (line.StartsWith("## ")) { branch = line[3..]; continue; }
            if (line.Length < 4) continue;
            string? original = null;
            if ((line[0] is 'R' or 'C' || line[1] is 'R' or 'C') && i + 1 < records.Length) original = records[++i];
            changes.Add(new(line[3..], original, line[0], line[1]));
        }
        return new(branch, changes);
    }

    public async Task<string[]> FilesAsync(CancellationToken token = default)
    {
        var result = (await RunAsync(["ls-files", "--cached", "--others", "--exclude-standard", "-z"], token)).EnsureSuccess();
        return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
    }

    public async Task StageAsync(IEnumerable<string> paths) => (await RunAsync(new[] { "add", "--" }.Concat(paths))).EnsureSuccess();

    public async Task UnstageAsync(IEnumerable<string> paths)
    {
        var hasHead = (await RunAsync(["rev-parse", "--verify", "HEAD"])).ExitCode == 0;
        var args = hasHead ? new[] { "restore", "--staged", "--" } : new[] { "rm", "--cached", "-f", "--" };
        (await RunAsync(args.Concat(paths))).EnsureSuccess();
    }

    public async Task CommitAsync(string message, bool amend = false)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("请填写提交说明。");
        var args = amend ? new[] { "commit", "--amend", "--file=-" } : new[] { "commit", "--file=-" };
        (await RunAsync(args, input: message)).EnsureSuccess();
    }

    private async Task<string> ReadVersionAsync(string revision, string path, CancellationToken token)
    {
        var result = await RunAsync(["show", $"{revision}:{path}"], token);
        if (result.ExitCode == 0) return result.Output;
        // A missing blob is a normal side of added/deleted files. Verify absence before accepting it.
        var exists = await RunAsync(["cat-file", "-e", $"{revision}:{path}"], token);
        if (exists.ExitCode != 0) return "";
        return result.EnsureSuccess().Output;
    }

    public async Task<GitDiff> DiffAsync(GitChange change, bool staged, CancellationToken token = default)
    {
        var oldPath = staged ? change.OriginalPath ?? change.Path : change.Path;
        var oldText = change.IsUntracked ? "" : await ReadVersionAsync(staged ? "HEAD" : "", oldPath, token);
        string newText;
        if (staged) newText = await ReadVersionAsync("", change.Path, token);
        else
        {
            var file = FileService.SafePath(Root, change.Path);
            newText = File.Exists(file) ? await FileService.ReadTextAsync(file, token) : "";
        }
        return new(oldText, newText, staged ? "HEAD" : "INDEX", staged ? "INDEX · 暂存区" : "WORKTREE · 工作区");
    }

    public async Task<GitDiff> ConflictSidesAsync(string path, CancellationToken token = default) =>
        new(await ReadVersionAsync(":2", path, token), await ReadVersionAsync(":3", path, token), "OURS · Git stage 2", "THEIRS · Git stage 3");

    public async Task<string> LogAsync(CancellationToken token = default)
    {
        var result = await RunAsync(["log", "--all", "--graph", "--decorate=short", "--date=short", "--format=%h  %ad  %d %s  · %an", "-150"], token);
        if (result.ExitCode != 0 && (await RunAsync(["rev-parse", "--verify", "HEAD"], token)).ExitCode != 0) return "尚无提交。暂存文件后创建第一次提交。";
        return result.EnsureSuccess().Output;
    }

    public async Task<string> ShowCommitAsync(string hash, CancellationToken token = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(hash, "\\A[0-9a-fA-F]{4,40}\\z")) throw new ArgumentException("无效的提交 ID。");
        return (await RunAsync(["show", "--format=fuller", "--stat", "--patch", "--find-renames", hash, "--"], token)).EnsureSuccess().Output;
    }
}

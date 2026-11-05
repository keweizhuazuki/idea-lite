using System.Text;

namespace IdeaLite.Core;

public sealed partial class GitService
{
    /// <summary>Commit the working-tree versions of exactly the chosen files, preserving other staged changes.</summary>
    public async Task CommitSelectedAsync(IEnumerable<string> selectedPaths, string message, bool amend = false)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("请填写提交说明。");
        var selected = selectedPaths.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0)
        {
            if (!amend) throw new InvalidOperationException("请先勾选本次提交的文件。");
            (await RunAsync(["commit", "--amend", "--only", "--file=-"], input: message)).EnsureSuccess();
            return;
        }
        foreach (var path in selected) FileService.SafePath(Root, path);
        var status = await StatusAsync();
        var changes = status.Changes.Where(c => selected.Contains(c.Path)).ToArray();
        if (changes.Length != selected.Count) throw new InvalidOperationException("文件状态已经变化，请刷新改动列表后重新选择。");
        if (changes.Any(c => c.IsConflict)) throw new InvalidOperationException("请先解决勾选文件的冲突。");
        var paths = changes.SelectMany(c => c.OriginalPath == null ? new[] { c.Path } : new[] { c.Path, c.OriginalPath })
            .Distinct(StringComparer.Ordinal).ToArray();
        var pathspec = string.Concat(paths.Select(path => ":(literal)" + path + '\0'));
        var mergePath = (await RunAsync(["rev-parse", "--git-path", "MERGE_HEAD"])).EnsureSuccess().Output.Trim();
        if (File.Exists(Path.Combine(Root, mergePath)))
        {
            if (amend) throw new InvalidOperationException("请先完成合并提交，再使用 Amend。");
            if (status.Changes.Any(c => !c.IsUntracked && !selected.Contains(c.Path)))
                throw new InvalidOperationException("合并提交需要包含所有合并更改，请勾选完整更改后提交。");
            (await RunAsync(["add", "--pathspec-from-file=-", "--pathspec-file-nul"], input: pathspec)).EnsureSuccess();
            await CommitAsync(message);
            return;
        }
        var untracked = changes.Where(c => c.IsUntracked).ToArray();
        if (untracked.Length > 0)
        {
            var additions = string.Concat(untracked.Select(c => ":(literal)" + c.Path + '\0'));
            (await RunAsync(["add", "--intent-to-add", "--pathspec-from-file=-", "--pathspec-file-nul"], input: additions)).EnsureSuccess();
        }
        var temporaryRoot = Environment.GetEnvironmentVariable("IDEALITE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(temporaryRoot))
            temporaryRoot = Directory.Exists(@"D:\") ? @"D:\Codex\work\IdeaLite" : Path.GetTempPath();
        Directory.CreateDirectory(temporaryRoot);
        var messageFile = Path.Combine(temporaryRoot, "commit-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(messageFile, message, new UTF8Encoding(false));
            string[] args = amend
                ? ["commit", "--amend", "--only", "--file=" + messageFile, "--pathspec-from-file=-", "--pathspec-file-nul"]
                : ["commit", "--only", "--file=" + messageFile, "--pathspec-from-file=-", "--pathspec-file-nul"];
            (await RunAsync(args, input: pathspec)).EnsureSuccess();
        }
        finally { File.Delete(messageFile); }
    }
}

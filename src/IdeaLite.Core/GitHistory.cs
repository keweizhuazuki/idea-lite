using System.Globalization;

namespace IdeaLite.Core;

public sealed record GitRef(string FullName, string Name, string Group, bool Current);
public sealed record GitRevision(string Hash, string[] Parents, string References, string Author,
    DateTimeOffset Date, string Subject)
{
    public string ShortHash => Hash[..Math.Min(8, Hash.Length)];
    public string DisplayDate => Date.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
}

public sealed record RevisionFile(string Path, string OldPath, char Status)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed partial class GitService
{
    public async Task<IReadOnlyList<GitRef>> ReferencesAsync(CancellationToken token = default)
    {
        var output = (await RunAsync(["for-each-ref", "--sort=refname", "--format=%(refname)%00%(HEAD)%00%(symref)",
            "refs/heads", "refs/remotes", "refs/tags"], token)).EnsureSuccess().Output;
        var result = new List<GitRef>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\0');
            if (fields.Length != 3 || fields[2].Length != 0) continue;
            var parts = fields[0].Split('/', 3);
            if (parts.Length != 3) continue;
            var group = parts[1] switch { "heads" => "本地", "remotes" => "远程", _ => "标签" };
            result.Add(new(fields[0], parts[2], group, fields[1] == "*"));
        }
        return result;
    }

    public async Task<IReadOnlyList<GitRevision>> RevisionsAsync(string? reference, CancellationToken token = default)
    {
        if (reference != null && reference != "HEAD" && !reference.StartsWith("refs/", StringComparison.Ordinal))
            throw new ArgumentException("请选择一个 Git 引用。");
        string[] args = ["log", "--topo-order", "--decorate=short", "-150", "-z",
            "--format=%H%x00%P%x00%D%x00%an%x00%aI%x00%s", reference ?? "--all", "--"];
        var result = await RunAsync(args, token);
        if (result.ExitCode != 0 && reference == "HEAD" && (await RunAsync(["rev-parse", "--verify", "HEAD"], token)).ExitCode != 0)
            return [];
        var fields = result.EnsureSuccess().Output.Split('\0');
        var revisions = new List<GitRevision>();
        for (var i = 0; i + 5 < fields.Length; i += 6)
        {
            revisions.Add(new(fields[i], fields[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                fields[i + 2], fields[i + 3], DateTimeOffset.Parse(fields[i + 4], CultureInfo.InvariantCulture), fields[i + 5]));
        }
        return revisions;
    }

    public async Task<IReadOnlyList<RevisionFile>> RevisionFilesAsync(GitRevision revision, CancellationToken token = default)
    {
        // Use the first parent for merges and the empty tree for a root commit.
        string[] args = revision.Parents.Length == 0
            ? ["diff-tree", "--root", "-r", "--no-commit-id", "--name-status", "-z", "-M", revision.Hash, "--"]
            : ["diff", "--name-status", "-z", "-M", revision.Parents[0], revision.Hash, "--"];
        var fields = (await RunAsync(args, token)).EnsureSuccess().Output.Split('\0');
        var files = new List<RevisionFile>();
        for (int i = 0; i + 1 < fields.Length && fields[i].Length > 0;)
        {
            var status = fields[i++][0];
            var oldPath = fields[i++];
            var path = status is 'R' or 'C' ? fields[i++] : oldPath;
            files.Add(new(path, oldPath, status));
        }
        return files;
    }

    public async Task<GitDiff> RevisionDiffAsync(GitRevision revision, RevisionFile file, CancellationToken token = default)
    {
        var old = revision.Parents.Length == 0 || file.Status == 'A'
            ? "" : await ReadVersionAsync(revision.Parents[0], file.OldPath, token);
        var current = file.Status == 'D' ? "" : await ReadVersionAsync(revision.Hash, file.Path, token);
        var left = revision.Parents.Length == 0 ? "空版本" : revision.Parents[0][..8];
        return new(old, current, left + "   " + file.OldPath, revision.ShortHash + "   " + file.Path);
    }
}

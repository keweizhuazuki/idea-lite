using System.Text;
using System.Text.RegularExpressions;

namespace IdeaLite.Core;

public sealed record CommitMessageContext(string Branch, string Changes, string Draft, bool Amend);

public sealed partial class GitService
{
    /// <summary>
    /// 提取勾选文件将提交的工作区内容；只读 Git，不改变索引或文件。
    /// </summary>
    public async Task<CommitMessageContext> CommitContextAsync(IEnumerable<string> selectedPaths, string draft = "", bool amend = false,
        CancellationToken token = default, bool stagedOnly = false)
    {
        const int limit = 16_000;
        var status = await StatusAsync(token);
        var selected = (stagedOnly ? status.Changes.Where(change => change.IsStaged).Select(change => change.Path) : selectedPaths)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0 && !amend) throw new InvalidOperationException("请先勾选本次提交的文件。");
        if (selected.Length > 100) throw new InvalidOperationException("AI 单次最多分析 100 个文件，请缩小勾选范围。");
        foreach (var path in selected) FileService.SafePath(Root, path);
        var changes = status.Changes.Where(change => selected.Contains(change.Path, StringComparer.Ordinal)).ToArray();
        if (changes.Length != selected.Length) throw new InvalidOperationException("文件状态已经变化，请刷新改动列表后重新选择。");
        if (changes.Any(change => change.IsConflict)) throw new InvalidOperationException("请先解决勾选文件的冲突。");
        var head = (await RunAsync(["rev-parse", "--verify", "HEAD"], token)).ExitCode == 0;
        if (amend && !head) throw new InvalidOperationException("尚无可修改的提交，请先创建第一次提交。");
        var text = new StringBuilder();
        static string Clip(string value, int budget)
        {
            if (value.Length <= budget) return value;
            const string marker = "\n[Diff truncated; omitted content is unknown]\n";
            int keep = Math.Max(0, budget - marker.Length);
            return value[..keep] + marker[..Math.Min(marker.Length, budget - keep)];
        }
        void Append(string value)
        {
            int remaining = limit - text.Length;
            if (remaining <= 0) return;
            text.Append(Clip(value, remaining));
        }
        Append((stagedOnly ? "Selected staged files from the Git index:\n" : "Selected working-tree files (not just the index):\n") +
            string.Join('\n', changes.Select(change => $"{change.Index}{(stagedOnly ? ' ' : change.WorkTree)} {change.Path}")) + "\n\n");
        if (amend)
        {
            Append("Prior commit to amend:\n" + Clip((await RunAsync(["log", "-1", "--format=%B"], token)).EnsureSuccess().Output, 1000));
            var previous = (await RunAsync(["show", "--format=", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=3", "HEAD", "--"], token)).EnsureSuccess();
            // 有新改动时优先保留本次差异；纯 Amend 仍能描述原提交。
            Append(Clip(previous.Output, selected.Length > 0 ? 3500 : 12_000));
        }
        var parts = new List<string>();
        var tracked = changes.Where(change => stagedOnly || !change.IsUntracked && head).ToArray();
        if (tracked.Length > 0)
        {
            var paths = tracked.SelectMany(change => change.OriginalPath == null ? new[] { change.Path } : new[] { change.Path, change.OriginalPath }).Distinct(StringComparer.Ordinal);
            var diff = (await RunAsync(new[] { "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=3", stagedOnly ? "--cached" : "HEAD", "--" }.Concat(paths.Select(path => ":(literal)" + path)), token)).EnsureSuccess();
            parts.AddRange(Regex.Split(diff.Output, @"(?m)(?=^diff --git )").Where(part => !string.IsNullOrWhiteSpace(part)));
        }
        foreach (var change in changes.Where(change => !stagedOnly && (change.IsUntracked || !head)))
        {
            token.ThrowIfCancellationRequested();
            var header = "\nNew file: " + change.Path + "\n";
            var path = FileService.SafePath(Root, change.Path);
            if (!File.Exists(path)) { parts.Add(header + "[File absent in working tree]\n"); continue; }
            try
            {
                var content = await FileService.ReadTextAsync(path, token);
                parts.Add(header + Clip(content, 4000) + "\n");
            }
            catch (IOException) { parts.Add(header + "[Binary, oversized or unreadable file: contents omitted]\n"); }
        }
        // 在文件间分配剩余预算，避免首个巨大 diff 挤掉后续文件。
        for (int index = 0; index < parts.Count && text.Length < limit; index++)
            Append(Clip(parts[index], (limit - text.Length) / (parts.Count - index)));
        var branch = status.Branch.Split("...", StringSplitOptions.None)[0];
        return new(branch.Length <= 256 ? branch : branch[..256], text.ToString(), draft.Length <= 1000 ? draft : draft[..1000], amend);
    }
}

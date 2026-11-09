using System.IO.Compression;
using System.Text.RegularExpressions;

namespace IdeaLite.Core;

public sealed record MavenSource(MavenDependency Dependency, string Entry, string Text);

public static class MavenSources
{
    public static MavenModule? ModuleFor(WorkspaceSnapshot workspace, string sourcePath) =>
        workspace.Maven.Where(module =>
        {
            int slash = module.Path.LastIndexOf('/');
            return slash < 0 || sourcePath.StartsWith(module.Path[..(slash + 1)], StringComparison.OrdinalIgnoreCase);
        }).MaxBy(module => module.Path.LastIndexOf('/'));

    public static Task<MavenSource?> FindAsync(MavenModule module, string qualifiedType,
        IEnumerable<string> repositories, CancellationToken token = default) => Task.Run<MavenSource?>(async () =>
    {
        const int limit = 512 * 1024;
        var entryName = qualifiedType.Replace('.', '/') + ".java";
        foreach (var repository in repositories)
        foreach (var dependency in module.Dependencies)
        {
            token.ThrowIfCancellationRequested();
            var group = dependency.Group.Split('.');
            if (!group.All(IsCoordinate) || !IsCoordinate(dependency.Artifact) || !IsCoordinate(dependency.Version)) continue;
            var path = Path.Combine(repository, Path.Combine(group), dependency.Artifact, dependency.Version,
                $"{dependency.Artifact}-{dependency.Version}-sources.jar");
            try
            {
                if (!File.Exists(path)) continue;
                using var archive = ZipFile.OpenRead(path);
                var entry = archive.GetEntry(entryName);
                if (entry == null || entry.Length > limit) continue;
                using var reader = new StreamReader(entry.Open());
                var buffer = new char[(int)entry.Length + 1];
                int count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
                if (count < buffer.Length) return new(dependency, entryName, new string(buffer, 0, count));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return null;
    }, token);

    private static bool IsCoordinate(string value) =>
        value is not ("." or "..") && Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]+\z");
}

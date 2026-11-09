using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace IdeaLite.Core;

public sealed record MavenDependency(string Group, string Artifact, string Version, string Scope)
{
    public override string ToString() => $"{Group}:{Artifact}:{Version} [{Scope}]";
}
public sealed record MavenModule(string Path, string Artifact, string Group, string Version,
    IReadOnlyList<string> Modules, IReadOnlyList<MavenDependency> Dependencies, IReadOnlyDictionary<string, string> Properties, string? Error = null)
{
    public IReadOnlyDictionary<(string Group, string Artifact), string> ManagedVersions { get; init; } =
        new Dictionary<(string, string), string>();
    public override string ToString() => $"{Artifact}  {Version}  · {Path}";
}

public static class MavenAnalyzer
{
    public static MavenModule Parse(string fullPath, string relativePath, MavenModule? workspaceParent = null)
    {
        if (new FileInfo(fullPath).Length > 1024 * 1024) throw new IOException("pom.xml 超过 1 MiB 限制。");
        using var reader = XmlReader.Create(fullPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var project = XDocument.Load(reader).Root ?? throw new XmlException("pom.xml 缺少 project。");
        string Value(XElement? element, string name) => element?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
        var parent = project.Elements().FirstOrDefault(e => e.Name.LocalName == "parent");
        var group = Value(project, "groupId"); if (group.Length == 0) group = Value(parent, "groupId");
        var version = Value(project, "version"); if (version.Length == 0) version = Value(parent, "version");
        var artifact = Value(project, "artifactId");
        var properties = workspaceParent?.Properties.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<string, string>();
        properties["project.groupId"] = group; properties["project.version"] = version; properties["project.artifactId"] = artifact;
        properties["pom.groupId"] = group; properties["pom.version"] = version;
        foreach (var element in project.Elements().FirstOrDefault(e => e.Name.LocalName == "properties")?.Elements() ?? []) properties[element.Name.LocalName] = element.Value.Trim();
        string Resolve(string value)
        {
            for (int i = 0; i < 8; i++)
            {
                var resolved = Regex.Replace(value, @"\$\{([^}]+)\}", m => properties.GetValueOrDefault(m.Groups[1].Value, m.Value));
                if (resolved == value) break;
                value = resolved;
            }
            return value;
        }
        var managed = new Dictionary<(string, string), string>();
        if (workspaceParent != null && Resolve(Value(parent, "groupId")) == workspaceParent.Group &&
            Resolve(Value(parent, "artifactId")) == workspaceParent.Artifact && Resolve(Value(parent, "version")) == workspaceParent.Version)
        {
            foreach (var entry in workspaceParent.ManagedVersions) managed.Add(entry.Key, entry.Value);
        }
        foreach (var item in project.Elements().FirstOrDefault(e => e.Name.LocalName == "dependencyManagement")?.Descendants().Where(e => e.Name.LocalName == "dependency") ?? [])
        {
            var managedVersion = Resolve(Value(item, "version"));
            if (managedVersion.Length > 0) managed[(Resolve(Value(item, "groupId")), Resolve(Value(item, "artifactId")))] = managedVersion;
        }
        var dependencies = new List<MavenDependency>();
        foreach (var item in project.Elements().FirstOrDefault(e => e.Name.LocalName == "dependencies")?.Elements().Where(e => e.Name.LocalName == "dependency") ?? [])
        {
            var g = Resolve(Value(item, "groupId")); var a = Resolve(Value(item, "artifactId")); var v = Resolve(Value(item, "version"));
            if (v.Length == 0) v = managed.GetValueOrDefault((g, a), "");
            dependencies.Add(new(g, a, v.Length == 0 ? "由父 POM / BOM 管理" : v, Value(item, "scope") is { Length: > 0 } scope ? scope : "compile"));
        }
        var modules = project.Elements().FirstOrDefault(e => e.Name.LocalName == "modules")?.Elements().Select(e => Resolve(e.Value.Trim())).ToArray() ?? [];
        return new(relativePath, artifact, Resolve(group), Resolve(version), modules, dependencies, properties) { ManagedVersions = managed };
    }
}

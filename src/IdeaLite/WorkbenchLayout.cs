using System.IO;
using System.Text.Json;

namespace IdeaLite;

public enum LeftTool { None, Project, Commit, Structure }
public enum BottomTool { None, Log, Console, LocalChanges, Services, Run }

public sealed class WorkbenchLayout
{
    public LeftTool Left { get; set; } = LeftTool.Project;
    public BottomTool Bottom { get; set; }
    public bool Maven { get; set; }
    public bool BottomCommit { get; set; }
    public double LeftWidth { get; set; } = 282;
    public double RightWidth { get; set; } = 250;
    public double BottomHeight { get; set; } = 255;
    public List<string> Recent { get; set; } = [];

    private static string Location
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("IDEALITE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(root))
                root = Directory.Exists(@"D:\") ? @"D:\Codex\IdeaLite" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdeaLite");
            return Path.Combine(root, "workbench.json");
        }
    }
    public static WorkbenchLayout Load()
    {
        try
        {
            var file = new FileInfo(Location);
            if (!file.Exists || file.Length > 64 * 1024) return new();
            var layout = JsonSerializer.Deserialize<WorkbenchLayout>(File.ReadAllText(file.FullName)) ?? new();
            layout.Recent ??= [];
            if (!Enum.IsDefined(layout.Left)) layout.Left = LeftTool.Project;
            if (!Enum.IsDefined(layout.Bottom)) layout.Bottom = BottomTool.None;
            return layout;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        var path = Location;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

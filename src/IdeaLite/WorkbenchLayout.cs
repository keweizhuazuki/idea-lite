using System.IO;
using System.Text.Json;
using IdeaLite.Core;

namespace IdeaLite;

public enum LeftTool { None, Project, Commit, Structure }
public enum BottomTool { None, Log, Console, LocalChanges, Services, Run }

public sealed class WorkbenchLayout
{
    public LeftTool Left { get; set; } = LeftTool.Project;
    public BottomTool Bottom { get; set; }
    public bool Maven { get; set; }
    public string Language { get; set; } = "en";
    public AiProvider CommitAiProvider { get; set; } = AiProvider.OpenAI;
    public string CommitAiLanguage { get; set; } = "en";
    public string CommitAiPrompt { get; set; } = AiCommitService.DefaultPrompt;
    public string OpenAiCommitModel { get; set; } = AiCommitService.DefaultModel(AiProvider.OpenAI);
    public string DeepSeekCommitModel { get; set; } = AiCommitService.DefaultModel(AiProvider.DeepSeek);
    public bool BottomCommit { get; set; }
    public double LeftWidth { get; set; } = 360;
    public double RightWidth { get; set; } = 250;
    public double BottomHeight { get; set; } = 255;
    public List<string> Recent { get; set; } = [];
    public bool MavenInWsl { get; set; }
    public string MavenExecutable { get; set; } = "";
    public string MavenSettings { get; set; } = "";
    public string MavenRepository { get; set; } = "";
    public string JavaHome { get; set; } = "";
    public string MavenGoals { get; set; } = "test";
    public Dictionary<string, string> MavenGoalsByProject { get; set; } = [];

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
            layout.Language = layout.Language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true ? "zh-CN" : "en";
            if (!Enum.IsDefined(layout.CommitAiProvider)) layout.CommitAiProvider = AiProvider.OpenAI;
            layout.CommitAiLanguage = layout.CommitAiLanguage == "zh-CN" ? "zh-CN" : "en";
            if (string.IsNullOrWhiteSpace(layout.CommitAiPrompt)) layout.CommitAiPrompt = AiCommitService.DefaultPrompt;
            if (string.IsNullOrWhiteSpace(layout.OpenAiCommitModel)) layout.OpenAiCommitModel = AiCommitService.DefaultModel(AiProvider.OpenAI);
            if (string.IsNullOrWhiteSpace(layout.DeepSeekCommitModel)) layout.DeepSeekCommitModel = AiCommitService.DefaultModel(AiProvider.DeepSeek);
            layout.MavenExecutable ??= ""; layout.MavenSettings ??= ""; layout.MavenRepository ??= ""; layout.JavaHome ??= "";
            if (string.IsNullOrWhiteSpace(layout.MavenGoals)) layout.MavenGoals = "test";
            layout.MavenGoalsByProject = new(layout.MavenGoalsByProject ?? [], StringComparer.OrdinalIgnoreCase);
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

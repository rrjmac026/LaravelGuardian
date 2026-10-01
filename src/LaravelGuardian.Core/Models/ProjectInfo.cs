// Models/ProjectInfo.cs
namespace LaravelGuardian.Core.Models;

public record ToolInfo(string Name, bool Found, string? Version);

public class ProjectInfo
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
    public bool IsLaravel { get; init; }
    public List<string> MissingItems { get; init; } = new();
    public string? LaravelVersion { get; init; }
    public bool HasPest { get; init; }
    public bool HasPhpUnit { get; init; }
    public bool UsesVite { get; init; }
    public string? FrontendFramework { get; init; } // React, Vue, or null
}
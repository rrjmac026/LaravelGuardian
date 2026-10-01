// Interfaces/IProjectScanner.cs
using LaravelGuardian.Core.Models;
namespace LaravelGuardian.Core.Interfaces;

public interface IProjectScanner
{
    Task<ProjectInfo> ScanAsync(string path, CancellationToken ct = default);
}

public interface IToolDetector
{
    Task<ToolInfo> DetectAsync(string name, string command, string args, CancellationToken ct = default);
}
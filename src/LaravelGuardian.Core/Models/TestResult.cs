// Models/TestResult.cs
namespace LaravelGuardian.Core.Models;

public class TestResult
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public TestStatus Status { get; set; }
    public Severity Severity { get; set; } = Severity.Info;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public TimeSpan Duration { get; set; }
    public string? Url { get; set; }
    public int? HttpStatus { get; set; }
    public string? Expected { get; set; }
    public string? Actual { get; set; }
    public string? Message { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
    public string? EvidencePath { get; set; }
    public string? ScreenshotPath { get; set; }
    public List<string> ConsoleErrors { get; set; } = new();
    public List<string> NetworkErrors { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}
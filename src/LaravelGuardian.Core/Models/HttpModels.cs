namespace LaravelGuardian.Core.Models;

public class HttpCheckOptions
{
    public int TimeoutSeconds { get; set; } = 30;
    public int SlowMs { get; set; } = 5000;
    public int DelayMs { get; set; } = 0;
    public int MaxRoutes { get; set; } = 200;

    /// Wildcard patterns on the URI, e.g. "admin/*"
    public List<string> ExcludePatterns { get; set; } = new();
}
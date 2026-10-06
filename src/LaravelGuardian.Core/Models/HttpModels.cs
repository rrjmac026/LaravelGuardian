namespace LaravelGuardian.Core.Models;

public class HttpCheckOptions
{
    public int TimeoutSeconds { get; set; } = 30;
    public int SlowMs { get; set; } = 5000;
    public int DelayMs { get; set; } = 0;
    public int MaxRoutes { get; set; } = 200;

    /// Wildcard patterns on the URI, e.g. "admin/*"
    public List<string> ExcludePatterns { get; set; } = new();

    /// Path words that should not be openable by a guest. Matched against whole path segments
    /// (split on / - _ .), so "debug" matches "debug/google-config" and "_debugbar" matches "debugbar".
    public List<string> RiskyPublicTokens { get; set; } = new()
    {
        "debug", "debugbar", "telescope", "phpinfo", "horizon", "pulse",
        "ignition", "adminer", "phpmyadmin"
    };
}
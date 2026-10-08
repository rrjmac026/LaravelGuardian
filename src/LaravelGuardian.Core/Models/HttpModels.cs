namespace LaravelGuardian.Core.Models;

public class HttpCheckOptions
{
    public int TimeoutSeconds { get; set; } = 30;
    public int SlowMs { get; set; } = 5000;
    public int DelayMs { get; set; } = 0;
    public int MaxRoutes { get; set; } = 200;
    /// Try parameter routes ({id}) with real values found in links on pages already fetched.
    public bool TestParameterRoutes { get; set; } = true;

    /// How many different real values to try per parameter route.
    public int MaxSamplesPerParameterRoute { get; set; } = 2;

    /// Same-origin link paths seen on fetched pages. Share ONE set across all role passes of a run.
    public HashSet<string> KnownLinks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// Wildcard patterns on the URI, e.g. "admin/*"
    public List<string> ExcludePatterns { get; set; } = new();

    /// Path words that should not be openable by a guest. Matched against whole path segments
    /// (split on / - _ .), so "debug" matches "debug/google-config" and "_debugbar" matches "debugbar".
    public List<string> RiskyPublicTokens { get; set; } = new()
    {
        "debug", "debugbar", "telescope", "phpinfo", "horizon", "pulse",
        "ignition", "adminer", "phpmyadmin"
    };

    /// Label of the role the logged-in session belongs to. Stored as Metadata["role"] on every
    /// result that was checked while logged in, so results and the access matrix can tell roles apart.
    public string? RoleLabel { get; set; }

    /// Extra role passes only need the routes behind login. When true, guest routes and skipped
    /// routes are left out of the results (the first pass already reported them).
    public bool AuthenticatedRoutesOnly { get; set; }
}
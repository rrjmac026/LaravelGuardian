namespace LaravelGuardian.Core.Models;

public class BrowserOptions
{
    public bool Headless { get; set; } = true;

    /// Hard cap on pages visited in one run.
    public int MaxPages { get; set; } = 80;

    /// Link depth below the seed pages (seeds are depth 0).
    public int MaxDepth { get; set; } = 3;

    public int NavigationTimeoutMs { get; set; } = 15000;

    /// Also visit every safe GET route from route:list, not only pages reachable by links.
    public bool SeedFromRoutes { get; set; } = true;

    /// Role the logged-in session belongs to. Added to every result's name and Metadata["role"]
    /// so the roles' results stay separate in Results and in run comparisons.
    public string? RoleLabel { get; set; }

    /// Wildcard path patterns to never visit, e.g. "admin/reports/*".
    public List<string> ExcludePatterns { get; set; } = new()
    {
        "up", "sanctum/*", ".well-known/*", "_*", "storage/*"
    };

    /// Max pages visited per URL pattern like /admin/audit-logs/{id}.
    public int MaxPerPattern { get; set; } = 2;
}
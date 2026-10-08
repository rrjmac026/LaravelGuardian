namespace LaravelGuardian.Core.Models;

public sealed record RunSummary(
    Guid Id, string ProjectName, string ProjectPath,
    DateTime StartedAt, DateTime UpdatedAt,
    int Total, int Passed, int Failed, int Warnings, int Skipped, int Blocked)
{
    public string StartedLocal => StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Label =>
        $"{Passed} pass · {Failed} fail · {Warnings} warn · {Blocked} blocked · {Skipped} skipped";
}

public sealed record ReportPaths(string Html, string Json);

public sealed class RunComparison
{
    public List<string> NewFailures { get; } = new();
    public List<string> StillFailing { get; } = new();
    public List<string> Fixed { get; } = new();
}

public static class RunComparer
{
    // Role is part of the key: the same route checked as admin and as student is two different results.
    private static string Key(TestResult r) =>
        $"{r.Metadata.GetValueOrDefault("source", "")}|{r.Metadata.GetValueOrDefault("role", "")}|{r.Name}";

    private static string Display(TestResult r) =>
        r.Metadata.TryGetValue("role", out var role) && !string.IsNullOrWhiteSpace(role)
            ? $"{r.Name} [{role}]"
            : r.Name;

    private static Dictionary<string, TestResult> Index(IEnumerable<TestResult> results)
    {
        var d = new Dictionary<string, TestResult>();
        foreach (var r in results) d[Key(r)] = r;   // last one wins on duplicate names
        return d;
    }

    public static RunComparison Compare(IEnumerable<TestResult> previous, IEnumerable<TestResult> current)
    {
        var prev = Index(previous);
        var cur = Index(current);
        var result = new RunComparison();

        foreach (var (key, r) in cur)
        {
            bool failsNow = r.Status == TestStatus.Fail;
            bool failedBefore = prev.TryGetValue(key, out var p) && p.Status == TestStatus.Fail;

            if (failsNow && failedBefore) result.StillFailing.Add(Display(r));
            else if (failsNow) result.NewFailures.Add(Display(r));
            else if (failedBefore && r.Status == TestStatus.Pass) result.Fixed.Add(Display(r));
        }
        return result;
    }
}

/// One route and what each role got when it opened it.
public sealed class RoleMatrixRow
{
    /// Result name, e.g. "GET /admin/users".
    public string Route { get; init; } = "";

    /// The URL path part of Route, e.g. "/admin/users".
    public string Path
    {
        get
        {
            var i = Route.IndexOf(' ');
            return i >= 0 ? Route[(i + 1)..] : Route;
        }
    }

    /// Role label -> RoleMatrix.Allowed / Forbidden / Error / Other. A missing role means "not checked".
    public Dictionary<string, string> Cells { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// Route x role table built from the logged-in HTTP results, plus simple access findings.
/// This only reports what each role was able to open; it does not know what SHOULD be allowed,
/// so the findings below are hints based on role names appearing in the URL (/admin/..., /student/...).
public sealed class RoleMatrix
{
    public const string Allowed = "allowed";
    public const string Forbidden = "forbidden";
    public const string Error = "error";
    public const string Other = "other";

    public List<string> Roles { get; } = new();
    public List<RoleMatrixRow> Rows { get; } = new();
    public bool IsEmpty => Roles.Count == 0 || Rows.Count == 0;

    /// Maps an HTTP classification to a matrix cell.
    public static string Cell(string classification) => classification switch
    {
        "ok" or "redirect" or "slow" => Allowed,
        "forbidden-for-role" => Forbidden,
        "server-error" or "not-found" or "timeout" or "connection-error" or "invalid-json" or "redirect-loop" => Error,
        _ => Other
    };

    /// Uses only results that were checked logged in and carry a role label.
    public static RoleMatrix Build(IEnumerable<TestResult> results)
    {
        var matrix = new RoleMatrix();
        var rows = new Dictionary<string, RoleMatrixRow>();

        foreach (var r in results)
        {
            if (r.Metadata.GetValueOrDefault("authenticated") != "true") continue;

            var role = r.Metadata.GetValueOrDefault("role", "");
            if (string.IsNullOrWhiteSpace(role)) continue;

            if (!matrix.Roles.Contains(role, StringComparer.OrdinalIgnoreCase)) matrix.Roles.Add(role);

            if (!rows.TryGetValue(r.Name, out var row))
            {
                row = new RoleMatrixRow { Route = r.Name };
                rows[r.Name] = row;
                matrix.Rows.Add(row);
            }
            row.Cells[role] = Cell(r.Metadata.GetValueOrDefault("classification", ""));
        }

        return matrix;
    }

    /// Findings as warnings (category "Authorization"):
    /// - role-leak: a path under /<role>/ can also be opened by another role
    /// - role-owner-denied: the role a path is named after gets 403 on it
    /// - role-nobody: every tested role gets 403 (dead route, or it belongs to an untested role)
    public List<TestResult> FindIssues()
    {
        var issues = new List<TestResult>();

        foreach (var row in Rows)
        {
            var owner = OwnerRole(row.Path);

            if (owner is not null)
            {
                var leaks = Roles
                    .Where(r => !r.Equals(owner, StringComparison.OrdinalIgnoreCase)
                                && row.Cells.GetValueOrDefault(r) == Allowed)
                    .ToList();

                if (leaks.Count > 0)
                    issues.Add(Issue(row, TestStatus.Warning, Severity.Medium, "role-leak",
                        $"{row.Path} is under /{owner} but can also be opened by: {string.Join(", ", leaks)}",
                        $"Only '{owner}' can open it", $"Also allowed for {string.Join(", ", leaks)}"));

                if (row.Cells.GetValueOrDefault(owner) == Forbidden)
                    issues.Add(Issue(row, TestStatus.Warning, Severity.Low, "role-owner-denied",
                        $"{row.Path} is under /{owner} but '{owner}' gets 403 on it",
                        $"'{owner}' can open it", "HTTP 403 for its own role"));
            }

            if (Roles.Count >= 2
                && Roles.All(r => row.Cells.GetValueOrDefault(r) == Forbidden))
                issues.Add(Issue(row, TestStatus.Warning, Severity.Low, "role-nobody",
                    $"{row.Path} is forbidden for every tested role ({string.Join(", ", Roles)}). " +
                    "It may belong to an untested role, or nobody can reach it.",
                    "At least one role can open it", "HTTP 403 for all tested roles"));
        }

        return issues;
    }

    private string? OwnerRole(string path)
    {
        var first = path.Trim('/').Split('/', 2)[0];
        return Roles.FirstOrDefault(r => r.Equals(first, StringComparison.OrdinalIgnoreCase));
    }

    private static TestResult Issue(
        RoleMatrixRow row, TestStatus status, Severity severity, string classification,
        string message, string expected, string actual)
    {
        var r = new TestResult
        {
            Category = "Authorization",
            Name = $"Role access {row.Route}",
            Status = status,
            Severity = severity,
            Message = message,
            Expected = expected,
            Actual = actual
        };
        r.Metadata["classification"] = classification;
        r.Metadata["matrix"] = string.Join(", ", row.Cells.Select(c => $"{c.Key}={c.Value}"));
        return r;
    }
}
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
    private static string Key(TestResult r) =>
        $"{r.Metadata.GetValueOrDefault("source", "")}|{r.Name}";

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

            if (failsNow && failedBefore) result.StillFailing.Add(r.Name);
            else if (failsNow) result.NewFailures.Add(r.Name);
            else if (failedBefore && r.Status == TestStatus.Pass) result.Fixed.Add(r.Name);
        }
        return result;
    }
}
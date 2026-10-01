using System.Text.Json;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

public class RouteScanner : IRouteScanner
{
    private readonly IArtisanRunner _artisan;

    public RouteScanner(IArtisanRunner artisan) => _artisan = artisan;

    public async Task<RouteScanResult> ScanAsync(ProjectInfo project, CancellationToken ct = default)
    {
        var result = new TestResult { Category = "Routes", Name = "Route discovery" };
        var scan = new RouteScanResult { Result = result };

        if (!File.Exists(Path.Combine(project.Path, "vendor", "autoload.php")))
        {
            Fail(result, TestStatus.Blocked, "vendor/ is missing. Run 'composer install' first.");
            return scan;
        }

        // streamOutput: false, because the JSON is huge and would flood the activity log
        var run = await _artisan.RunAsync(project.Path, "route:list --json --no-ansi",
            TimeSpan.FromSeconds(60), streamOutput: false, ct);
        result.Duration = run.Duration;

        if (!run.Started) { Fail(result, TestStatus.Blocked, $"Could not start PHP: {run.Stderr.Trim()}"); return scan; }
        if (run.TimedOut) { Fail(result, TestStatus.Fail, "route:list timed out after 60 seconds."); return scan; }

        var json = ExtractJsonArray(run.Stdout);
        if (run.ExitCode != 0 || json is null)
        {
            Fail(result, TestStatus.Fail, $"route:list failed (exit code {run.ExitCode}).");
            result.ExceptionMessage = (run.Stderr + Environment.NewLine + run.Stdout).Trim();
            if (result.ExceptionMessage.Length > 2000) result.ExceptionMessage = result.ExceptionMessage[..2000];
            return scan;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var methods = (Str(el, "method") ?? "")
                    .Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Where(m => m != "HEAD").ToList();

                var route = new RouteInfo
                {
                    Methods = methods,
                    Uri = Str(el, "uri") ?? "",
                    Name = Str(el, "name"),
                    Action = Str(el, "action"),
                    Domain = Str(el, "domain")
                };

                if (el.TryGetProperty("middleware", out var mw))
                {
                    if (mw.ValueKind == JsonValueKind.Array)
                        route.Middleware = mw.EnumerateArray()
                            .Select(m => m.GetString() ?? "").Where(m => m.Length > 0).ToList();
                    else if (mw.ValueKind == JsonValueKind.String)
                        route.Middleware = new List<string> { mw.GetString()! };
                }

                scan.Routes.Add(route);
            }
        }
        catch (JsonException ex)
        {
            Fail(result, TestStatus.Fail, $"Could not parse route:list output: {ex.Message}");
            return scan;
        }

        result.Status = TestStatus.Pass;
        result.Message = $"{scan.Routes.Count} routes discovered";
        return scan;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// Skips any PHP warnings/deprecations printed before the JSON.
    private static string? ExtractJsonArray(string stdout)
    {
        var start = stdout.IndexOf("[{", StringComparison.Ordinal);
        if (start < 0)
            return stdout.Contains("[]") ? "[]" : null;
        var end = stdout.LastIndexOf(']');
        return end > start ? stdout[start..(end + 1)] : null;
    }

    private static void Fail(TestResult r, TestStatus status, string message)
    {
        r.Status = status;
        r.Severity = Severity.High;
        r.Message = message;
    }
}
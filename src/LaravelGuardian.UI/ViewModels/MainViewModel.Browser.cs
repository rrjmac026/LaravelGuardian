using CommunityToolkit.Mvvm.Input;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace LaravelGuardian.UI.ViewModels;

/// The "Run Browser Checks" button (Playwright / Chromium). Own file, same pattern as Migrations.
public partial class MainViewModel
{
    // Same rule as the HTTP button: needs a Laravel project, a running environment, and no other run.
    partial void OnIsEnvironmentRunningChanged(bool value) => RunBrowserChecksCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanRunHttp))]
    private async Task RunBrowserChecksAsync()
    {
        var project = Project;
        var baseUrl = _env.BaseUrl;
        if (project is null || string.IsNullOrEmpty(baseUrl)) return;

        await RunCancellableAsync(async ct =>
        {
            if (DiscoveredRoutes.Count == 0)
            {
                Log("No routes yet, discovering them first...");
                if (!await DiscoverRoutesCoreAsync(project, ct)) return;
            }

            // One login per ticked role (same 3-failure cap as the HTTP run), then one crawl per role.
            var sessions = await LoginAllRolesAsync(baseUrl, ct);

            Log($"Running browser checks against {baseUrl} (Chromium, GET navigation only)...");

            var seenIssues = new HashSet<string>();
            var runner = _services.GetRequiredService<IBrowserCheckRunner>();
            var all = new List<TestResult>();

            void Show(TestResult r)
            {
                if (r.Status == TestStatus.Skipped) return;

                Log($"  {r.Status.ToString().ToUpper(),-8} {r.Name}  " +
                    $"[{r.HttpStatus?.ToString() ?? "-"}]  {r.Duration.TotalMilliseconds:0} ms  {r.Message}");

                // Uncaught JavaScript errors: the full text of up to 3, each shown once per run.
                if (r.Metadata.TryGetValue("pageExceptions", out var js) && !string.IsNullOrWhiteSpace(js))
                    foreach (var e in js.Split('\n').Select(x => "js: " + x).Take(3))
                        if (seenIssues.Add(e)) Log("      " + e);

                foreach (var e in r.ConsoleErrors.Select(x => "console: " + x)
                            .Concat(r.NetworkErrors.Select(x => "network: " + x)).Take(6))
                    if (seenIssues.Add(e)) Log("      " + e);
            }

            // No login worked: a single guest crawl, as before.
            var passes = sessions.Count == 0
                ? new List<AuthSession?> { null }
                : sessions.Select(x => (AuthSession?)x).ToList();

            foreach (var s in passes)
            {
                ct.ThrowIfCancellationRequested();
                if (s is not null) Log($"--- Role: {s.Role} ---");

                var options = new BrowserOptions { RoleLabel = s?.Role };
                var results = await runner.RunAsync(baseUrl, DiscoveredRoutes, options, s,
                    _session.CurrentRunId, Show, Log, ct);
                all.AddRange(results);

                // Chromium could not start, or the server is gone: more passes would fail the same way.
                if (results.Any(r => r.Status == TestStatus.Blocked))
                {
                    Log("  Browser checks were blocked, so the remaining roles were not run.");
                    break;
                }
            }

            await SaveAsync("browser", all);
            LogBrowserSummary(all);
        });
    }

    /// The browser crawl still uses one account (admin first) until it gets its own per-role pass.
    private async Task<AuthSession?> LoginForChecksAsync(string baseUrl, CancellationToken ct)
    {
        var account = PrimaryAccount();
        if (account is null)
        {
            Log("No test account ticked (with email and password), so pages that need login will not be visited.");
            return null;
        }

        var email = account.Email.Trim();
        var label = RoleLabel(account);

        Log($"Logging in as {email} [{label}] (the only POST Guardian sends)...");
        var attempt = await _login.LoginAsync(baseUrl, email, account.Password, ct);
        attempt.Role = label;
        attempt.Result.Name = $"Login ({label})";
        attempt.Result.Metadata["role"] = label;
        Log($"  {attempt.Result.Status.ToString().ToUpper(),-8} {attempt.Result.Name}: {attempt.Message}");
        await SaveAsync("auth", new[] { attempt.Result });

        if (attempt.Success) return attempt;

        Log("  Continuing with guest pages only.");
        return null;
    }

    private void LogBrowserSummary(IReadOnlyList<TestResult> results)
    {
        var tested = results.Where(r => r.Status != TestStatus.Skipped).ToList();
        int Count(TestStatus s) => tested.Count(r => r.Status == s);

        Log($"Browser checks: {tested.Count} checked | {Count(TestStatus.Pass)} passed | " +
            $"{Count(TestStatus.Fail)} failed | {Count(TestStatus.Warning)} warnings | " +
            $"{Count(TestStatus.Blocked)} blocked");

        var byClass = tested
            .GroupBy(r => r.Metadata.GetValueOrDefault("classification", "other"))
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}");
        if (tested.Count > 0) Log("  Breakdown: " + string.Join(", ", byClass));

        foreach (var g in tested.Where(r => r.Metadata.ContainsKey("role")).GroupBy(r => r.Metadata["role"]))
            Log($"  [{g.Key}] {g.Count()} page(s): {g.Count(x => x.Status == TestStatus.Pass)} passed, " +
                $"{g.Count(x => x.Status == TestStatus.Fail)} failed, {g.Count(x => x.Status == TestStatus.Warning)} warnings");

        var repeated = tested.SelectMany(r => r.ConsoleErrors.Concat(r.NetworkErrors).Distinct())
            .GroupBy(e => e).Where(g => g.Count() > 1).OrderByDescending(g => g.Count()).Take(5);
        foreach (var g in repeated) Log($"  Repeated on {g.Count()} pages: {g.Key}");

        var shots = tested.Count(r => !string.IsNullOrEmpty(r.ScreenshotPath));
        if (shots > 0) Log($"  Screenshots saved for {shots} page(s); see them in the evidence folder of this run.");

        var skipped = results.Where(r => r.Status == TestStatus.Skipped).ToList();
        foreach (var g in skipped.GroupBy(r => r.Metadata.GetValueOrDefault("skipReason", "other")))
            Log($"  Not tested ({g.Count()}, {g.Key}): {g.First().Message}" +
                (g.Count() > 1 ? $" (e.g. {g.First().Name})" : ""));
    }
}
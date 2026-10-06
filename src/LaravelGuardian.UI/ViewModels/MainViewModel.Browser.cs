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

            var session = await LoginForChecksAsync(baseUrl, ct);

            Log($"Running browser checks against {baseUrl} (Chromium, GET navigation only)...");

            var seenIssues = new HashSet<string>();
            var runner = _services.GetRequiredService<IBrowserCheckRunner>();
            var results = await runner.RunAsync(baseUrl, DiscoveredRoutes, new BrowserOptions(), session,
                _session.CurrentRunId,
                r =>
                {
                    if (r.Status == TestStatus.Skipped) return;

                    Log($"  {r.Status.ToString().ToUpper(),-8} {r.Name}  " +
                        $"[{r.HttpStatus?.ToString() ?? "-"}]  {r.Duration.TotalMilliseconds:0} ms  {r.Message}");

                    foreach (var e in r.ConsoleErrors.Select(x => "console: " + x)
                                .Concat(r.NetworkErrors.Select(x => "network: " + x)).Take(6))
                    if (seenIssues.Add(e)) Log("      " + e);
                },
                Log, ct);

            await SaveAsync("browser", results);
            LogBrowserSummary(results);
            
        });
    }

    private async Task<AuthSession?> LoginForChecksAsync(string baseUrl, CancellationToken ct)
    {
        var email = AccountEmail.Trim();
        if (email.Length == 0 || string.IsNullOrEmpty(AccountPassword))
        {
            Log("No test account set, so pages that need login will not be visited.");
            return null;
        }

        Log($"Logging in as {email} (the only POST Guardian sends)...");
        var attempt = await _login.LoginAsync(baseUrl, email, AccountPassword, ct);
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

        var repeated = tested.SelectMany(r => r.ConsoleErrors.Concat(r.NetworkErrors).Distinct())
            .GroupBy(e => e).Where(g => g.Count() > 1).OrderByDescending(g => g.Count()).Take(5);
        foreach (var g in repeated) Log($"  Repeated on {g.Count()} pages: {g.Key}");

        var shots = tested.Count(r => !string.IsNullOrEmpty(r.ScreenshotPath));
        if (shots > 0) Log($"  Screenshots saved for {shots} page(s); see them in the evidence folder of this run.");

        foreach (var s in results.Where(r => r.Status == TestStatus.Skipped))
            Log($"  Not tested: {s.Name}: {s.Message}");

        
    }
}
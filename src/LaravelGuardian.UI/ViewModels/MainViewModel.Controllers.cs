using CommunityToolkit.Mvvm.Input;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace LaravelGuardian.UI.ViewModels;

/// The "Check Controllers" button. Kept in its own file, like the migrations check.
/// The shared state notifications live in MainViewModel.Migrations.cs.
public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanRunChecks))]
    private async Task CheckControllersAsync()
    {
        var project = Project;
        if (project is null) return;

        await RunCancellableAsync(async ct =>
        {
            if (DiscoveredRoutes.Count == 0)
            {
                Log("No routes yet, discovering them first...");
                if (!await DiscoverRoutesCoreAsync(project, ct)) return;
            }

            Log("Checking that every route's controller method exists (read-only, no server needed)...");

            var checker = _services.GetRequiredService<IControllerChecker>();
            var results = await checker.CheckAsync(project, DiscoveredRoutes, ct);

            LogControllerSummary(results);
            await SaveAsync("controllers", results.ToArray());
        });
    }

    private void LogControllerSummary(IReadOnlyList<TestResult> results)
    {
        var checkedResults = results.Where(r => r.Status != TestStatus.Skipped).ToList();
        int Count(TestStatus s) => checkedResults.Count(r => r.Status == s);

        Log($"Controller check: {checkedResults.Count} controller methods | {Count(TestStatus.Pass)} found | " +
            $"{Count(TestStatus.Fail)} missing | {Count(TestStatus.Warning)} uncertain");

        foreach (var r in checkedResults.Where(r => r.Status is TestStatus.Fail or TestStatus.Warning).Take(30))
        {
            var routes = r.Metadata.GetValueOrDefault("routes", "");
            Log($"  {r.Status.ToString().ToUpper(),-8} {r.Name}  {r.Message}");
            if (routes.Length > 0) Log($"           used by: {routes}");
        }

        var problems = checkedResults.Count(r => r.Status is TestStatus.Fail or TestStatus.Warning);
        if (problems > 30) Log($"  ...and {problems - 30} more (see Results)");

        var skipped = results.Where(r => r.Status == TestStatus.Skipped).ToList();
        if (skipped.Count > 0)
            Log("  Not checked: " + string.Join(", ", skipped.Select(r => r.Message)));
    }
}
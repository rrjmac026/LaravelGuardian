using CommunityToolkit.Mvvm.Input;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace LaravelGuardian.UI.ViewModels;

/// The "Check Migrations" button. Kept in its own file so MainViewModel.cs stays smaller.
public partial class MainViewModel
{
    // The attributes in MainViewModel.cs do not know about this command,
    // so re-check the button whenever the shared state changes.
    partial void OnIsBusyChanged(bool value)
    {
        RunBrowserChecksCommand.NotifyCanExecuteChanged();
        CheckMigrationsCommand.NotifyCanExecuteChanged();
        CheckControllersCommand.NotifyCanExecuteChanged();
    }
    partial void OnProjectChanged(ProjectInfo? value)
    {
        RunBrowserChecksCommand.NotifyCanExecuteChanged();
        CheckMigrationsCommand.NotifyCanExecuteChanged();
        CheckControllersCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRunChecks))]
    private async Task CheckMigrationsAsync()
    {
        var project = Project;
        if (project is null) return;

        await RunCancellableAsync(async ct =>
        {
            Log("Checking migrations (php artisan migrate:status, read-only)...");

            var checker = _services.GetRequiredService<IMigrationChecker>();
            var result = await checker.CheckAsync(project, ct);

            await SaveAsync("migrations", new[] { result });
            Log($"{result.Status.ToString().ToUpper(),-8} {result.Name}: {result.Message}");

            if (result.Metadata.TryGetValue("pending", out var pending) && !string.IsNullOrWhiteSpace(pending))
            {
                var names = pending.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var name in names.Take(10)) Log("    pending: " + name);
                if (names.Length > 10) Log($"    ...and {names.Length - 10} more");
            }
        });
    }
}
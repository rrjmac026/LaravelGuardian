using System.Diagnostics;
using System.Text.RegularExpressions;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

/// Read-only: asks Laravel which migrations have run (php artisan migrate:status).
/// Guardian never runs 'migrate' itself.
public class MigrationChecker : IMigrationChecker
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex MigrationLine = new(
        @"^\s*(?<name>\d{4}_\d{2}_\d{2}_\d{6}_\S+)\s+\.+\s+(?<state>.+?)\s*$", RegexOptions.Compiled);

    private readonly IArtisanRunner _artisan;

    public MigrationChecker(IArtisanRunner artisan) => _artisan = artisan;

    public async Task<TestResult> CheckAsync(ProjectInfo project, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        TestResult Make(TestStatus status, Severity severity, string message) => new()
        {
            Category = "Migrations",
            Name = "migrate:status",
            Status = status,
            Severity = severity,
            Duration = sw.Elapsed,
            Message = message,
            Expected = "Every migration has run on the project's database"
        };

        if (!File.Exists(Path.Combine(project.Path, "vendor", "autoload.php")))
            return Make(TestStatus.Blocked, Severity.High, "vendor/ is missing. Run 'composer install' first.");

        if (!Directory.Exists(Path.Combine(project.Path, "database", "migrations")))
            return Make(TestStatus.Skipped, Severity.Info, "No database/migrations folder in this project.");

        var run = await _artisan.RunAsync(project.Path, "migrate:status --no-ansi",
            TimeSpan.FromSeconds(60), false, ct);

        if (!run.Started)
            return Make(TestStatus.Blocked, Severity.High, $"Could not start PHP: {run.Stderr.Trim()}");

        if (run.TimedOut)
            return Make(TestStatus.Blocked, Severity.High,
                "migrate:status did not answer within 60 seconds (is the database reachable?).");

        var output = Ansi.Replace(run.Stdout + "\n" + run.Stderr, "");

        if (output.Contains("Migration table not found", StringComparison.OrdinalIgnoreCase))
            return Make(TestStatus.Fail, Severity.High,
                "The database has no migrations table: migrations have never been run on it. " +
                "Run 'php artisan migrate' in the project (Guardian never does this itself).");

        int ran = 0;
        var pending = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var m = MigrationLine.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;

            var state = m.Groups["state"].Value;
            if (state.Contains("Pending", StringComparison.OrdinalIgnoreCase)) pending.Add(m.Groups["name"].Value);
            else if (state.Contains("Ran", StringComparison.OrdinalIgnoreCase)) ran++;
        }

        if (ran + pending.Count == 0)
        {
            if (run.ExitCode != 0)
                return Make(TestStatus.Blocked, Severity.High,
                    "Could not read the migration status: " + FirstError(output));

            return Make(TestStatus.Skipped, Severity.Info, "No migrations were reported.");
        }

        TestResult result;
        if (pending.Count > 0)
        {
            result = Make(TestStatus.Warning, Severity.Medium,
                $"{pending.Count} migration(s) have not run, so the database is behind the code ({ran} have run). " +
                "Run 'php artisan migrate' in the project if this is not intended.");
            result.Metadata["pending"] = string.Join("\n", pending);
        }
        else
        {
            result = Make(TestStatus.Pass, Severity.Info, $"All {ran} migrations have run.");
        }

        result.Metadata["ran"] = ran.ToString();
        result.Metadata["pendingCount"] = pending.Count.ToString();
        return result;
    }

    /// The most useful single line of an artisan failure (database errors first).
    private static string FirstError(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var line = lines.FirstOrDefault(l => l.Contains("SQLSTATE", StringComparison.OrdinalIgnoreCase))
                   ?? lines.FirstOrDefault(l => l.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                                             || l.Contains("ERROR", StringComparison.Ordinal))
                   ?? lines.FirstOrDefault()
                   ?? "no output";
        return line.Length > 240 ? line[..240] + "..." : line;
    }
}
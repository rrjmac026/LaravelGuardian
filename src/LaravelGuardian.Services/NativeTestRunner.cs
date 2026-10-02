using System.Globalization;
using System.Xml.Linq;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

public class NativeTestRunner : INativeTestRunner
{
    private readonly IArtisanRunner _artisan;

    public NativeTestRunner(IArtisanRunner artisan) => _artisan = artisan;

    public async Task<IReadOnlyList<TestResult>> RunAsync(
        ProjectInfo project, bool allowSharedDatabase = false, CancellationToken ct = default)
    {
        if (!project.HasPest && !project.HasPhpUnit)
            return One(TestStatus.Skipped, Severity.Info, "No Pest or PHPUnit found in composer.json.");

        if (!File.Exists(Path.Combine(project.Path, "vendor", "autoload.php")))
            return One(TestStatus.Blocked, Severity.High, "vendor/ is missing. Run 'composer install' first.");

        if (!allowSharedDatabase && !HasIsolatedDatabase(project.Path, out var reason))
            return One(TestStatus.Blocked, Severity.High,
                $"{reason} Running tests could wipe your development database (RefreshDatabase). " +
                "Set DB_CONNECTION/DB_DATABASE in phpunit.xml (e.g. sqlite + :memory:), " +
                "or tick 'Allow tests on the project's database' to run anyway.");

        var workDir = Path.Combine(Path.GetTempPath(), "LaravelGuardian", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var junit = Path.Combine(workDir, "junit.xml");

        try
        {
            var run = await _artisan.RunAsync(project.Path,
                $"test --no-ansi --log-junit=\"{junit}\"", TimeSpan.FromMinutes(10), true, ct);

            if (!run.Started)
                return One(TestStatus.Blocked, Severity.High, $"Could not start PHP: {run.Stderr.Trim()}");

            if (run.TimedOut)
                return One(TestStatus.Fail, Severity.High, "Test run timed out after 10 minutes and was stopped.");

            if (!File.Exists(junit))
            {
                var r = One(TestStatus.Fail, Severity.High,
                    $"Test run produced no report (exit code {run.ExitCode}).")[0];
                r.ExceptionMessage = Tail(run.Stdout + Environment.NewLine + run.Stderr);
                return new[] { r };
            }

            var results = Parse(junit);

            if (results.Count == 0)
                results.AddRange(One(TestStatus.Warning, Severity.Medium, "No tests were found."));
            else if (run.ExitCode != 0 && results.All(x => x.Status != TestStatus.Fail))
            {
                var w = One(TestStatus.Warning, Severity.Medium,
                    $"Test process exited with code {run.ExitCode} but no failing test was reported.")[0];
                w.ExceptionMessage = Tail(run.Stdout + Environment.NewLine + run.Stderr);
                results.Add(w);
            }

            return results;
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    private static List<TestResult> Parse(string junitPath)
    {
        var results = new List<TestResult>();
        var doc = XDocument.Load(junitPath);

        foreach (var tc in doc.Descendants("testcase"))
        {
            var name = (string?)tc.Attribute("name") ?? "";
            var cls = (string?)tc.Attribute("classname") ?? (string?)tc.Attribute("class") ?? "";
            double.TryParse((string?)tc.Attribute("time"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds);

            var r = new TestResult
            {
                Category = "Native tests",
                Name = string.IsNullOrEmpty(cls) ? name : $"{cls}::{name}",
                Duration = TimeSpan.FromSeconds(seconds)
            };
            var file = (string?)tc.Attribute("file");
            if (!string.IsNullOrEmpty(file)) r.Metadata["file"] = file;

            var problem = tc.Element("failure") ?? tc.Element("error");
            if (problem is not null)
            {
                r.Status = TestStatus.Fail;
                r.Severity = Severity.High;
                r.ExceptionType = (string?)problem.Attribute("type");
                var text = problem.Value.Trim();

                // Pest repeats the test name at the start of the failure text.
                // Strip it from each line and use the first line that still has content.
                var msg = "";
                foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = raw.Trim();
                    if (name.Length > 0 && line.StartsWith(name, StringComparison.Ordinal))
                        line = line[name.Length..].Trim();
                    if (line.Length > 0) { msg = line; break; }
                }
                if (msg.Length > 200) msg = msg[..200] + "...";
                r.Message = msg;

                r.ExceptionMessage = text.Length > 4000 ? text[..4000] : text;
            }
            else if (tc.Element("warning") is { } warning)
            {
                r.Status = TestStatus.Warning;
                r.Severity = Severity.Medium;
                r.Message = warning.Value.Trim().Split('\n').FirstOrDefault()?.Trim();
            }
            else if (tc.Element("skipped") is not null)
            {
                r.Status = TestStatus.Skipped;
            }
            else
            {
                r.Status = TestStatus.Pass;
            }

            results.Add(r);
        }
        return results;
    }

    /// Heuristic: tests must override the DB in phpunit.xml, or have a .env.testing file.
    private static bool HasIsolatedDatabase(string projectPath, out string reason)
    {
        reason = "";
        if (File.Exists(Path.Combine(projectPath, ".env.testing"))) return true;

        foreach (var file in new[] { "phpunit.xml", "phpunit.xml.dist" })
        {
            var full = Path.Combine(projectPath, file);
            if (!File.Exists(full)) continue;
            try
            {
                var names = XDocument.Load(full).Descendants()
                    .Where(e => e.Name.LocalName is "env" or "server")
                    .Select(e => (string?)e.Attribute("name"));
                if (names.Any(n => n is "DB_CONNECTION" or "DB_DATABASE")) return true;
                reason = $"{file} does not set DB_CONNECTION or DB_DATABASE for tests.";
                return false;
            }
            catch
            {
                reason = $"{file} could not be parsed.";
                return false;
            }
        }

        reason = "No phpunit.xml found.";
        return false;
    }

    private static TestResult[] One(TestStatus status, Severity severity, string message) => new[]
    {
        new TestResult
        {
            Category = "Native tests", Name = "php artisan test",
            Status = status, Severity = severity, Message = message
        }
    };

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(15);
        var joined = string.Join(Environment.NewLine, lines);
        return joined.Length > 2000 ? joined[^2000..] : joined;
    }
}
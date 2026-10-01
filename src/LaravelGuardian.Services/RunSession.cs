using System.Text.Json;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

public sealed class RunSession : IRunSession
{
    private readonly IRunStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _projectPath;

    public RunSession(IRunStore store) => _store = store;

    public Guid? CurrentRunId { get; private set; }

    public void NewRun()
    {
        CurrentRunId = null;
        _projectPath = null;
    }

    public async Task RecordAsync(string source, string projectPath,
        IReadOnlyList<TestResult> results, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // A run is per project: switching project starts a fresh run.
            if (CurrentRunId is null ||
                !string.Equals(_projectPath, projectPath, StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectPath));
                CurrentRunId = await _store.CreateRunAsync(name, projectPath);
                _projectPath = projectPath;
            }

            var runId = CurrentRunId.Value;

            foreach (var r in results)
            {
                r.Metadata["source"] = source;
                if (r.Status is TestStatus.Fail or TestStatus.Warning or TestStatus.Blocked)
                    r.EvidencePath = await WriteEvidenceAsync(runId, source, r, ct);
            }

            var replaced = await _store.ReplaceSourceAsync(runId, source, results);
            foreach (var path in replaced)
            {
                try { File.Delete(path); } catch { /* stale evidence, not critical */ }
            }
        }
        finally { _gate.Release(); }
    }

    private static async Task<string> WriteEvidenceAsync(
        Guid runId, string source, TestResult r, CancellationToken ct)
    {
        var dir = AppPaths.EvidenceDir(runId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{r.Id:N}.json");

        var doc = new
        {
            r.Id,
            source,
            r.Name,
            status = r.Status.ToString(),
            severity = r.Severity.ToString(),
            r.Url,
            r.HttpStatus,
            r.Expected,
            r.Actual,
            r.Message,
            r.ExceptionType,
            r.ExceptionMessage,
            r.ConsoleErrors,
            r.NetworkErrors,
            r.Metadata,
            capturedAtUtc = DateTime.UtcNow
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, JsonDefaults.Pretty), ct);
        return path;
    }
}
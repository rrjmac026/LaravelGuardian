using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Core.Interfaces;

public interface IRunStore
{
    Task<Guid> CreateRunAsync(string projectName, string projectPath);

    /// Replaces all results of one source (tests/routes/http/environment) in a run.
    /// Returns the evidence files of the replaced rows so the caller can delete them.
    Task<IReadOnlyList<string>> ReplaceSourceAsync(Guid runId, string source, IReadOnlyList<TestResult> results);

    Task<IReadOnlyList<RunSummary>> GetRunsAsync(int limit = 100);
    Task<RunSummary?> GetRunAsync(Guid runId);
    Task<IReadOnlyList<TestResult>> GetResultsAsync(Guid runId);
}

public interface IRunSession
{
    Guid? CurrentRunId { get; }
    void NewRun();
    Task RecordAsync(string source, string projectPath, IReadOnlyList<TestResult> results,
        CancellationToken ct = default);
}

public interface IReportExporter
{
    Task<ReportPaths> ExportAsync(Guid runId, CancellationToken ct = default);
}
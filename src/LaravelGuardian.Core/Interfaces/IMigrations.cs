using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Core.Interfaces;

public interface IMigrationChecker
{
    Task<TestResult> CheckAsync(ProjectInfo project, CancellationToken ct = default);
}
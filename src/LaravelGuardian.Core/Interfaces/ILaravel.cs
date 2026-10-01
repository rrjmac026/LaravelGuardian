using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Core.Interfaces;

public interface IArtisanRunner
{
    Task<CommandResult> RunAsync(string projectPath, string arguments, TimeSpan timeout,
        bool streamOutput = true, CancellationToken ct = default);
}

public interface INativeTestRunner
{
    Task<IReadOnlyList<TestResult>> RunAsync(ProjectInfo project, bool allowSharedDatabase = false,
        CancellationToken ct = default);
}

public interface IRouteScanner
{
    Task<RouteScanResult> ScanAsync(ProjectInfo project, CancellationToken ct = default);
}
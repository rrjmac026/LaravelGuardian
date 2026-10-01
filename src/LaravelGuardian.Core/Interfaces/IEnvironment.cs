using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Core.Interfaces;

public interface IProcessManager
{
    /// name, line, isError
    event Action<string, string, bool>? OutputReceived;
    /// name, exitCode
    event Action<string, int>? ProcessExited;

    int Start(string name, string fileName, string arguments, string workingDirectory);

    /// Runs a command to completion. Tracked, so StopAllAsync also kills it.
    Task<CommandResult> RunAsync(string name, string fileName, string arguments, string workingDirectory,
        TimeSpan timeout, bool streamOutput = true, CancellationToken ct = default);

    bool IsRunning(string name);
    Task StopAllAsync();
}

public interface IEnvironmentManager
{
    string? BaseUrl { get; }
    bool IsRunning { get; }
    Task<IReadOnlyList<TestResult>> StartAsync(ProjectInfo project, bool startVite, CancellationToken ct = default);
    Task StopAsync();
}
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

public class ArtisanRunner : IArtisanRunner
{
    private readonly IProcessManager _processes;

    public ArtisanRunner(IProcessManager processes) => _processes = processes;

    public Task<CommandResult> RunAsync(string projectPath, string arguments, TimeSpan timeout,
        bool streamOutput = true, CancellationToken ct = default)
    {
        var first = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return _processes.RunAsync($"artisan:{first}", "php", $"artisan {arguments}",
            projectPath, timeout, streamOutput, ct);
    }
}
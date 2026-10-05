using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Core.Interfaces;

public interface IControllerChecker
{
    Task<IReadOnlyList<TestResult>> CheckAsync(
        ProjectInfo project,
        IReadOnlyList<RouteInfo> routes,
        CancellationToken ct = default);
}
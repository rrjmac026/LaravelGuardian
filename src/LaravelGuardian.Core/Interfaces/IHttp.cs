using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Core.Interfaces;

public interface IHttpCheckRunner
{
    Task<IReadOnlyList<TestResult>> RunAsync(
        string baseUrl,
        IReadOnlyList<RouteInfo> routes,
        HttpCheckOptions options,
        Action<TestResult>? onResult = null,
        CancellationToken ct = default);
}
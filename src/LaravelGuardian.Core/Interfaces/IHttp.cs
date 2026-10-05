using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Core.Interfaces;

public interface IHttpCheckRunner
{
    /// auth: when it holds a successful login, routes that need authentication are checked with it.
    Task<IReadOnlyList<TestResult>> RunAsync(
        string baseUrl,
        IReadOnlyList<RouteInfo> routes,
        HttpCheckOptions options,
        AuthSession? auth = null,
        Action<TestResult>? onResult = null,
        CancellationToken ct = default);
}
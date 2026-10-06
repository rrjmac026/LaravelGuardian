using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Core.Interfaces;

public interface IBrowserCheckRunner
{
    /// Crawls the app in a real browser (GET navigation only) and reports one result per page.
    /// auth: when it holds a successful login, its cookies are loaded into the browser.
    /// runId: used only to decide where failure screenshots are stored.
    Task<IReadOnlyList<TestResult>> RunAsync(
        string baseUrl,
        IReadOnlyList<RouteInfo> routes,
        BrowserOptions options,
        AuthSession? auth = null,
        Guid? runId = null,
        Action<TestResult>? onResult = null,
        Action<string>? onLog = null,
        CancellationToken ct = default);
}
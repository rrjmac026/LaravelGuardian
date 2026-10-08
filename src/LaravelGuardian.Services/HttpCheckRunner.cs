using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

/// Safe-mode HTTP checks. This file holds the run logic; response analysis is in HttpCheckRunner.Response.cs.
public partial class HttpCheckRunner : IHttpCheckRunner
{
    private const int MaxRedirects = 5;
    private const int RepeatedErrorLimit = 3;

    internal static readonly HashSet<string> DangerousTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "logout", "signout", "delete", "destroy", "remove", "wipe", "truncate", "purge",
        "seed", "migrate", "artisan", "impersonate", "backup", "export", "download"
    };

    // Extra caution once logged in: GET routes with these words often change data.
    internal static readonly HashSet<string> AuthDangerousTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "approve", "reject", "cancel", "clear", "reset", "revoke", "toggle", "sync", "send",
        "restore", "archive", "activate", "deactivate", "mark", "publish", "unpublish"
    };

    public async Task<IReadOnlyList<TestResult>> RunAsync(
    string baseUrl, IReadOnlyList<RouteInfo> routes, HttpCheckOptions options,
    AuthSession? auth = null, Action<TestResult>? onResult = null, CancellationToken ct = default)
    {
        var results = new List<TestResult>();
        var baseUri = new Uri(baseUrl);

        var canAuth = auth is { Success: true, Cookies: not null };

        using var guestHttp = CreateClient(options, null);                        // fresh guest, no cookies
        using var authHttp = canAuth ? CreateClient(options, auth!.Cookies) : null; // logged-in session

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paramRoutes = new List<RouteInfo>();
        int tested = 0;

        string? lastSignature = null;
        int repeat = 0;
        string? abortReason = null;
        bool blocked = false;

        void Report(TestResult r) { results.Add(r); onResult?.Invoke(r); }

        // Returns true when the whole run must stop (server unreachable).
        bool AfterCheck(TestResult result)
        {
            if (result.Status == TestStatus.Blocked) { blocked = true; return true; }

            // Same server error several times in a row = probably one shared cause, not many bugs.
            var signature = ErrorSignature(result);
            if (signature is not null && signature == lastSignature) repeat++;
            else { lastSignature = signature; repeat = signature is null ? 0 : 1; }

            if (repeat >= RepeatedErrorLimit)
            {
                var what = result.ExceptionType ?? "server error";
                if (!string.IsNullOrWhiteSpace(result.ExceptionMessage))
                    what += " - " + Shorten(result.ExceptionMessage!, 120);
                abortReason = $"Not run: {repeat} routes in a row failed with the same error ({what}). " +
                            "These failures probably share one cause. Fix that first, then run again.";
            }
            return false;
        }

        foreach (var route in routes)
        {
            ct.ThrowIfCancellationRequested();

            // Extra role passes only look at routes behind login (guest routes were reported by the first pass).
            if (options.AuthenticatedRoutesOnly && !route.RequiresAuth) continue;

            var skip = GetSkipReason(route, options, canAuth);

            // Parameter routes are tried after the normal pass, once real values have been collected.
            if (skip is { Code: "parameters" } && options.TestParameterRoutes)
            {
                paramRoutes.Add(route);
                continue;
            }

            if (skip is null)
            {
                var key = "/" + route.Uri.TrimStart('/');
                if (!seen.Add(key)) skip = ("Duplicate URL already checked", "duplicate");
                else if (tested >= options.MaxRoutes) skip = ($"Route limit ({options.MaxRoutes}) reached", "limit");
            }

            if (skip is not null)
            {
                if (options.AuthenticatedRoutesOnly) continue; // the first pass already reported skips
                Report(Skipped(route, skip.Value.Reason, skip.Value.Code));
                continue;
            }

            if (abortReason is not null)
            {
                // Tag the role only when this route was (or would have been) checked logged in, so the report can tell the passes apart.
                Report(NotRun(route, abortReason, route.RequiresAuth && canAuth ? options.RoleLabel : null));
                continue;
            }

            tested++;
            var authed = route.RequiresAuth && authHttp is not null;
            var client = authed ? authHttp! : guestHttp;
            var result = await CheckAsync(client, baseUri, route, options, authed, ct);
            Report(result);

            if (AfterCheck(result)) break; // server unreachable, no point continuing
            if (options.DelayMs > 0) await Task.Delay(options.DelayMs, ct);
        }

        // ---------- parameter routes, using real values found on the pages above ----------
        if (paramRoutes.Count > 0)
        {
            var staticPaths = new HashSet<string>(
                routes.Where(r => !r.HasParameters).Select(r => "/" + r.Uri.TrimStart('/')),
                StringComparer.OrdinalIgnoreCase);

            foreach (var route in paramRoutes)
            {
                ct.ThrowIfCancellationRequested();

                if (blocked || abortReason is not null)
                {
                    if (!options.AuthenticatedRoutesOnly)
                        Report(Skipped(route, "Has route parameters; the run stopped before they could be tried", "parameters"));
                    continue;
                }

                var paths = FindConcretePaths(route, options.KnownLinks, staticPaths, options.MaxSamplesPerParameterRoute);
                if (paths.Count == 0)
                {
                    if (!options.AuthenticatedRoutesOnly)
                        Report(Skipped(route, "Has route parameters; no real value was found in the pages Guardian opened", "parameters"));
                    continue;
                }

                foreach (var path in paths)
                {
                    var concrete = WithUri(route, path.TrimStart('/'));
                    var skip = GetSkipReason(concrete, options, canAuth);
                    if (skip is null)
                    {
                        if (!seen.Add(path)) skip = ("Duplicate URL already checked", "duplicate");
                        else if (tested >= options.MaxRoutes) skip = ($"Route limit ({options.MaxRoutes}) reached", "limit");
                    }

                    if (skip is not null)
                    {
                        if (!options.AuthenticatedRoutesOnly) Report(Skipped(concrete, skip.Value.Reason, skip.Value.Code));
                        continue;
                    }

                    tested++;
                    var authed = concrete.RequiresAuth && authHttp is not null;
                    var client = authed ? authHttp! : guestHttp;
                    var result = await CheckAsync(client, baseUri, concrete, options, authed, ct);
                    result.Metadata["routePattern"] = "/" + route.Uri.TrimStart('/');
                    Report(result);

                    if (AfterCheck(result)) break;
                    if (abortReason is not null) break;
                    if (options.DelayMs > 0) await Task.Delay(options.DelayMs, ct);
                }
            }
        }

        return results;
    }

    private static HttpClient CreateClient(HttpCheckOptions options, CookieContainer? cookies)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,   // we follow redirects ourselves to classify them
            UseCookies = cookies is not null,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        if (cookies is not null) handler.CookieContainer = cookies;

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LaravelGuardian/0.1");
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        return http;
    }

    // ---------- one route ----------

    private async Task<TestResult> CheckAsync(
        HttpClient http, Uri baseUri, RouteInfo route, HttpCheckOptions o, bool authed, CancellationToken ct)
    {
        var path = route.Uri == "/" ? "/" : "/" + route.Uri.TrimStart('/');
        var url = new Uri(baseUri, path).ToString();

        var result = new TestResult
        {
            Category = route.IsApi ? "API" : "Routes",
            Name = $"GET {path}",
            Url = url,
            Expected = authed
                ? "2xx for the logged-in test account"
                : "2xx, or a redirect to a working page"
        };
        if (!string.IsNullOrEmpty(route.Name)) result.Metadata["routeName"] = route.Name!;
        if (!string.IsNullOrEmpty(route.Action)) result.Metadata["action"] = route.Action!;
        if (authed)
        {
            result.Metadata["authenticated"] = "true";
            if (!string.IsNullOrWhiteSpace(o.RoleLabel)) result.Metadata["role"] = o.RoleLabel!;
        }

        var sw = Stopwatch.StartNew();
        var hops = new List<string>();
        var visited = new HashSet<string> { url };
        var current = url;

        TestResult Done(TestStatus status, Severity severity, string classification, string message)
        {
            result.Status = status;
            result.Severity = severity;
            result.Duration = sw.Elapsed;
            result.Message = message;
            result.Actual = result.HttpStatus?.ToString() ?? classification;
            result.Metadata["classification"] = classification;
            if (hops.Count > 0) result.Metadata["redirects"] = string.Join(" | ", hops);
            return result;
        }

        void Attach(HttpResponseMessage r, string body, string contentType)
        {
            result.Metadata["headers"] = FormatHeaders(r);
            result.Metadata["contentType"] = contentType;
            result.Metadata["bodySnippet"] = Snippet(body, contentType, 2000);
        }

        try
        {
            for (int hop = 0; ; hop++)
            {
                using var response = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
                var status = (int)response.StatusCode;
                result.HttpStatus = status;

                // ----- redirects -----
                if (status is >= 300 and < 400)
                {

                    if (response.Headers.Location is not { } location)
                        return Done(TestStatus.Warning, Severity.Low, "redirect-no-location",
                            $"HTTP {status} without a Location header");

                    var next = new Uri(new Uri(current), location);
                    hops.Add($"{status} -> {next.PathAndQuery}");

                    if (!SameOrigin(next, baseUri))
                        return Done(TestStatus.Pass, Severity.Info, "redirect-external",
                            $"Redirects to a different origin ({next.GetLeftPart(UriPartial.Authority)}); not followed");

                    if (IsLoginPath(next) && !IsLoginPath(new Uri(current)))
                    {
                        if (authed)
                            return Done(TestStatus.Warning, Severity.Medium, "session-lost",
                                $"Redirected to {next.AbsolutePath} even with a logged-in session");

                        return Done(TestStatus.Pass, Severity.Info, "auth-redirect",
                            $"Guest is redirected to {next.AbsolutePath}");
                    }

                    if (!visited.Add(next.ToString()) || hop >= MaxRedirects)
                        return Done(TestStatus.Fail, Severity.Medium, "redirect-loop",
                            "Redirect loop or too many redirects: " + string.Join(" , ", hops));

                    current = next.ToString();
                    continue;
                }

                // ----- final response -----
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                var (body, truncated) = await ReadBodyAsync(response, ct);
                var via = hops.Count > 0 ? $" after {hops.Count} redirect(s)" : "";
                var prefix = hops.Count > 0 ? "Redirect target: " : "";

                if (status is >= 200 and < 300)
                {
                    if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                        CollectLinks(body, new Uri(current), baseUri, o.KnownLinks);
                        
                    if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                        && !truncated && !string.IsNullOrWhiteSpace(body) && !IsValidJson(body))
                    {
                        Attach(response, body, contentType);
                        return Done(TestStatus.Fail, Severity.Medium, "invalid-json",
                            "Response is declared as JSON but cannot be parsed");
                    }

                    if (route.IsApi && contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    {
                        Attach(response, body, contentType);
                        return Done(TestStatus.Warning, Severity.Low, "api-returned-html",
                            $"HTTP {status}{via}: API route returned HTML instead of JSON");
                    }

                    // A guest got a real page from a path that normally belongs behind protection.
                    if (!authed && hops.Count == 0 && IsRiskyPublic(route.Uri, o))
                    {
                        Attach(response, body, contentType);
                        return Done(TestStatus.Warning, Severity.Medium, "risky-public",
                            $"HTTP {status}: a guest can open this path, but debug or admin-style routes " +
                            "should be protected or removed");
                    }

                    if (sw.ElapsedMilliseconds > o.SlowMs)
                        return Done(TestStatus.Warning, Severity.Low, "slow",
                            $"HTTP {status}{via}, but slow ({sw.ElapsedMilliseconds} ms)");

                    return Done(TestStatus.Pass, Severity.Info, hops.Count > 0 ? "redirect" : "ok",
                        $"HTTP {status}{via}");
                }

                if (status == 401 || status == 403)
                {
                    if (authed)
                    {
                        // 403 with a logged-in account = role separation working (expected).
                        if (status == 403)
                            return Done(TestStatus.Pass, Severity.Info, "forbidden-for-role",
                                $"HTTP 403{via}: this test account's role is not allowed here (expected)");

                        // 401 with a session means the session was lost: a real problem.
                        Attach(response, body, contentType);
                        return Done(TestStatus.Warning, Severity.Medium, "session-lost",
                            $"HTTP 401{via} even with a logged-in session");
                    }

                    return Done(TestStatus.Pass, Severity.Info, status == 401 ? "unauthorized" : "forbidden",
                        $"HTTP {status}{via}: access denied for an unauthenticated guest");
                }

                Attach(response, body, contentType);

                if (status == 404)
                    return Done(TestStatus.Fail, Severity.Medium, "not-found",
                        $"{prefix}Route is registered but returned 404");

                if (status == 429)
                    return Done(TestStatus.Warning, Severity.Low, "rate-limited", $"{prefix}HTTP 429 (rate limited)");

                if (status == 503)
                    return Done(TestStatus.Warning, Severity.Medium, "unavailable",
                        $"{prefix}HTTP 503 (maintenance mode or overloaded)");

                if (status >= 500)
                {
                    var (type, message) = DetectException(body, contentType);
                    result.ExceptionType = type;
                    result.ExceptionMessage = message;

                    var detail = type is not null ? $": {type}" : "";
                    if (!string.IsNullOrWhiteSpace(message))
                        detail += (type is not null ? " - " : ": ") + Shorten(message, 160);

                    return Done(TestStatus.Fail, Severity.High, "server-error",
                        $"{prefix}HTTP {status}{detail}");
                }

                return Done(TestStatus.Warning, Severity.Low, "client-error", $"{prefix}HTTP {status}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Done(TestStatus.Fail, Severity.Medium, "timeout", $"No response within {o.TimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            // Stop the whole run only if the server is really gone.
            return await IsListeningAsync(new Uri(baseUri, "/"), ct)
                ? Done(TestStatus.Fail, Severity.High, "connection-error",
                    $"The server closed the connection without a proper response ({ex.Message})")
                : Done(TestStatus.Blocked, Severity.High, "unreachable",
                    $"Could not reach the server ({ex.Message}). Remaining HTTP checks were not run.");
        }
    }

    // ---------- skipping rules (Safe Mode) ----------

    internal static (string Reason, string Code)? GetSkipReason(RouteInfo route, HttpCheckOptions o, bool canAuth)
    {
        if (!string.IsNullOrEmpty(route.Domain))
            return ($"Domain-bound route ({route.Domain})", "domain");

        if (!route.IsGet)
            return ($"Safe Mode: {string.Join("|", route.Methods)} is state-changing and is not sent", "method");

        if (route.HasParameters)
            return ("Has route parameters; needs real values", "parameters");

        if (route.RequiresAuth && !canAuth)
            return ("Requires login; no logged-in test account was used", "auth");

        var tokens = route.Uri.Split(new[] { '/', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(DangerousTokens.Contains))
            return ("Excluded: path looks destructive or heavy for a GET", "excluded");

        if (route.RequiresAuth && tokens.Any(AuthDangerousTokens.Contains))
            return ("Excluded: while logged in, this GET path may change data", "excluded");

        if (MatchesExclude(route.Uri, o.ExcludePatterns))
            return ("Excluded by pattern", "excluded");

        return null;
    }

    /// True when the path contains a word from HttpCheckOptions.RiskyPublicTokens (whole segments only).
    private static bool IsRiskyPublic(string uri, HttpCheckOptions o)
    {
        if (o.RiskyPublicTokens.Count == 0) return false;
        var tokens = uri.Split(new[] { '/', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        var risky = new HashSet<string>(o.RiskyPublicTokens, StringComparer.OrdinalIgnoreCase);
        return tokens.Any(risky.Contains);
    }

    private static bool MatchesExclude(string uri, IEnumerable<string> patterns)
    {
        var u = uri.Trim('/');
        foreach (var p in patterns)
        {
            var rx = "^" + Regex.Escape(p.Trim('/')).Replace("\\*", ".*") + "$";
            if (Regex.IsMatch(u, rx, RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    private static TestResult Skipped(RouteInfo route, string reason, string code)
    {
        var path = route.Uri == "/" ? "/" : "/" + route.Uri.TrimStart('/');
        var r = new TestResult
        {
            Category = route.IsApi ? "API" : "Routes",
            Name = $"{string.Join("|", route.Methods)} {path}",
            Status = TestStatus.Skipped,
            Severity = Severity.Info,
            Message = reason
        };
        r.Metadata["skipReason"] = code;
        return r;
    }

    private static TestResult NotRun(RouteInfo route, string reason, string? role = null)
    {
        var path = route.Uri == "/" ? "/" : "/" + route.Uri.TrimStart('/');
        var r = new TestResult
        {
            Category = route.IsApi ? "API" : "Routes",
            Name = $"GET {path}",
            Status = TestStatus.Blocked,
            Severity = Severity.Info,
            Message = reason
        };
        r.Metadata["classification"] = "aborted";
        if (!string.IsNullOrWhiteSpace(role)) r.Metadata["role"] = role!;
        return r;
    }

    /// Identifies "the same server error" across routes; null when there is no usable detail.
    private static string? ErrorSignature(TestResult r)
    {
        if (r.Metadata.GetValueOrDefault("classification") != "server-error") return null;
        if (string.IsNullOrWhiteSpace(r.ExceptionType) && string.IsNullOrWhiteSpace(r.ExceptionMessage)) return null;
        return $"{r.ExceptionType}|{r.ExceptionMessage}";
    }

    // ---------- small URL helpers ----------

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static bool IsLoginPath(Uri u)
    {
        var p = u.AbsolutePath.ToLowerInvariant();
        return p.Contains("login") || p.Contains("signin") || p.Contains("sign-in");
    }

    private static async Task<bool> IsListeningAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(uri.Host, uri.Port, cts.Token);
            return true;
        }
        catch { return false; }
    }
}
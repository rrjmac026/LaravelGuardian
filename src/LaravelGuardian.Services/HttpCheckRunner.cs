using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

public class HttpCheckRunner : IHttpCheckRunner
{
    private const int MaxRedirects = 5;
    private const int MaxBodyBytes = 1_000_000;

    private static readonly HashSet<string> DangerousTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "logout", "signout", "delete", "destroy", "remove", "wipe", "truncate", "purge",
        "seed", "migrate", "artisan", "impersonate", "backup", "export", "download"
    };

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "set-cookie", "cookie", "authorization", "proxy-authorization", "x-csrf-token", "x-xsrf-token"
    };

    private static readonly Regex ExceptionClass = new(
        @"\b(?:[A-Z][A-Za-z0-9_]*\\)+[A-Z][A-Za-z0-9_]*(?:Exception|Error)\b", RegexOptions.Compiled);
    private static readonly Regex Scripts = new(
        @"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Title = new(
        @"<title[^>]*>(.*?)</title>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Secrets = new(
        @"(?i)((?:_token|csrf-token|password|secret|api[_-]?key|authorization|cookie)[""']?\s*(?:[:=]|content=|value=)\s*[""'])[^""']+",
        RegexOptions.Compiled);

    public async Task<IReadOnlyList<TestResult>> RunAsync(
        string baseUrl, IReadOnlyList<RouteInfo> routes, HttpCheckOptions options,
        Action<TestResult>? onResult = null, CancellationToken ct = default)
    {
        var results = new List<TestResult>();
        var baseUri = new Uri(baseUrl);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,   // we follow redirects ourselves to classify them
            UseCookies = false,          // every request is a fresh guest
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LaravelGuardian/0.1");
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int tested = 0;

        foreach (var route in routes)
        {
            ct.ThrowIfCancellationRequested();

            var skip = GetSkipReason(route, options);
            if (skip is null)
            {
                var key = "/" + route.Uri.TrimStart('/');
                if (!seen.Add(key)) skip = ("Duplicate URL already checked", "duplicate");
                else if (tested >= options.MaxRoutes) skip = ($"Route limit ({options.MaxRoutes}) reached", "limit");
            }

            if (skip is not null)
            {
                var s = Skipped(route, skip.Value.Reason, skip.Value.Code);
                results.Add(s);
                onResult?.Invoke(s);
                continue;
            }

            tested++;
            var result = await CheckAsync(http, baseUri, route, options, ct);
            results.Add(result);
            onResult?.Invoke(result);

            if (result.Status == TestStatus.Blocked) break; // server unreachable, no point continuing
            if (options.DelayMs > 0) await Task.Delay(options.DelayMs, ct);
        }

        return results;
    }

    // ---------- one route ----------

    private async Task<TestResult> CheckAsync(
        HttpClient http, Uri baseUri, RouteInfo route, HttpCheckOptions o, CancellationToken ct)
    {
        var path = route.Uri == "/" ? "/" : "/" + route.Uri.TrimStart('/');
        var url = new Uri(baseUri, path).ToString();

        var result = new TestResult
        {
            Category = route.IsApi ? "API" : "Routes",
            Name = $"GET {path}",
            Url = url,
            Expected = "2xx, or a redirect to a working page"
        };
        if (!string.IsNullOrEmpty(route.Name)) result.Metadata["routeName"] = route.Name!;
        if (!string.IsNullOrEmpty(route.Action)) result.Metadata["action"] = route.Action!;

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
                        return Done(TestStatus.Pass, Severity.Info, "auth-redirect",
                            $"Guest is redirected to {next.AbsolutePath}");

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

                    if (sw.ElapsedMilliseconds > o.SlowMs)
                        return Done(TestStatus.Warning, Severity.Low, "slow",
                            $"HTTP {status}{via}, but slow ({sw.ElapsedMilliseconds} ms)");

                    return Done(TestStatus.Pass, Severity.Info, hops.Count > 0 ? "redirect" : "ok",
                        $"HTTP {status}{via}");
                }

                if (status == 401 || status == 403)
                    return Done(TestStatus.Pass, Severity.Info, status == 401 ? "unauthorized" : "forbidden",
                        $"HTTP {status}{via}: access denied for an unauthenticated guest");

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
            return Done(TestStatus.Blocked, Severity.High, "unreachable",
                $"Could not reach the server ({ex.Message}). Remaining HTTP checks were not run.");
        }
    }

    // ---------- skipping rules (Safe Mode) ----------

    private static (string Reason, string Code)? GetSkipReason(RouteInfo route, HttpCheckOptions o)
    {
        if (!string.IsNullOrEmpty(route.Domain))
            return ($"Domain-bound route ({route.Domain})", "domain");

        if (!route.IsGet)
            return ($"Safe Mode: {string.Join("|", route.Methods)} is state-changing and is not sent", "method");

        if (route.HasParameters)
            return ("Has route parameters; needs real values", "parameters");

        if (route.RequiresAuth)
            return ("Requires authentication (test accounts come in a later step)", "auth");

        var tokens = route.Uri.Split(new[] { '/', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(DangerousTokens.Contains))
            return ("Excluded: path looks destructive or heavy for a GET", "excluded");

        if (MatchesExclude(route.Uri, o.ExcludePatterns))
            return ("Excluded by pattern", "excluded");

        return null;
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

    // ---------- helpers ----------

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static bool IsLoginPath(Uri u)
    {
        var p = u.AbsolutePath.ToLowerInvariant();
        return p.Contains("login") || p.Contains("signin") || p.Contains("sign-in");
    }

    private static bool IsValidJson(string body)
    {
        try { using var _ = JsonDocument.Parse(body); return true; }
        catch (JsonException) { return false; }
    }

    private static async Task<(string Body, bool Truncated)> ReadBodyAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBodyBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (n == 0) break;
            total += n;
        }
        var truncated = total > MaxBodyBytes;
        return (Encoding.UTF8.GetString(buffer, 0, Math.Min(total, MaxBodyBytes)), truncated);
    }

    private static (string? Type, string? Message) DetectException(string body, string contentType)
    {
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    string? Str(string n) =>
                        doc.RootElement.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String
                            ? p.GetString() : null;
                    var type = Str("exception");
                    var message = Str("message");
                    if (type is not null || message is not null) return (type, message);
                }
            }
            catch (JsonException) { }
        }

        // Best-effort: the first exception-looking class name in a debug error page
        var m = ExceptionClass.Match(body.Length > 50_000 ? body[..50_000] : body);
        return (m.Success ? m.Value : null, null);
    }

    private static string FormatHeaders(HttpResponseMessage r) =>
        string.Join("\n", r.Headers.Concat(r.Content.Headers).Select(h =>
            $"{h.Key}: {(SensitiveHeaders.Contains(h.Key) ? "[redacted]" : string.Join(", ", h.Value))}"));

    /// Best-effort, privacy-limited snippet: HTML becomes title + plain text, secrets are masked.
    private static string Snippet(string body, string contentType, int max)
    {
        string text;
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            var title = Title.Match(body);
            var stripped = Spaces.Replace(Tags.Replace(Scripts.Replace(body, " "), " "), " ").Trim();
            text = (title.Success ? title.Groups[1].Value.Trim() + " | " : "") + stripped;
        }
        else
        {
            text = body;
        }

        text = Secrets.Replace(text, "$1[redacted]");
        return text.Length > max ? text[..max] + "..." : text;
    }
}
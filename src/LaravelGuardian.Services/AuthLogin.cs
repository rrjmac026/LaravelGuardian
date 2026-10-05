using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

/// Logs in once through the app's normal /login form. This is the only POST Guardian sends,
/// and only to localhost / 127.0.0.1. The password is never logged or put in a result.
public class AuthLogin : IAuthLogin
{
    private const int MaxHops = 5;
    private const int MaxBodyChars = 500_000;

    private static readonly Regex InputTag = new(@"<input\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MetaTag = new(@"<meta\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TokenName = new(@"name\s*=\s*[""']_token[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CsrfName = new(@"name\s*=\s*[""']csrf-token[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ValueAttr = new(@"value\s*=\s*[""']([^""']*)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ContentAttr = new(@"content\s*=\s*[""']([^""']*)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<AuthSession> LoginAsync(
        string baseUrl, string email, string password, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var session = new AuthSession { Email = email };

        AuthSession Fail(string classification, string message)
        {
            session.Success = false;
            session.Cookies = null;
            session.Message = message;
            session.Result = Build(TestStatus.Blocked, Severity.Medium, classification, message, sw, email);
            return session;
        }

        AuthSession Ok(CookieContainer cookies, string message)
        {
            session.Success = true;
            session.Cookies = cookies;
            session.Message = message;
            session.Result = Build(TestStatus.Pass, Severity.Info, "login-ok", message, sw, email);
            return session;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return Fail("bad-url", "The environment has no valid base URL.");

        if (!IsLocalHost(baseUri.Host))
            return Fail("not-local", "Guardian only logs in on localhost / 127.0.0.1, never on a remote server.");

        var container = new CookieContainer();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = container,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LaravelGuardian/0.1");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");

        var loginUrl = new Uri(baseUri, "/login");

        try
        {
            // 1. Load the login page for the CSRF token and session cookie.
            string html;
            using (var page = await http.GetAsync(loginUrl, ct))
            {
                var pageStatus = (int)page.StatusCode;
                if (pageStatus is >= 300 and < 400)
                    return Fail("login-page", $"/login redirected (HTTP {pageStatus}); the app may not use a standard login form.");
                if (!page.IsSuccessStatusCode)
                    return Fail("login-page", $"/login returned HTTP {pageStatus}, so the login could not be tried.");

                html = await page.Content.ReadAsStringAsync(ct);
                if (html.Length > MaxBodyChars) html = html[..MaxBodyChars];
            }

            var token = FindToken(html);
            if (token is null)
                return Fail("no-csrf", "No CSRF token found on /login (non-standard login form or a SPA).");

            // 2. The one POST: the normal login form.
            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("_token", token),
                new KeyValuePair<string, string>("email", email),
                new KeyValuePair<string, string>("password", password)
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, loginUrl) { Content = form };
            request.Headers.Referrer = loginUrl;

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;

            if (status == 419)
                return Fail("csrf-expired", "Login rejected with HTTP 419 (CSRF/session). Check SESSION_DOMAIN and APP_URL in the project's .env.");
            if (status == 429)
                return Fail("throttled", "Login is rate limited (HTTP 429). Wait a minute and try again.");

            if (status is not (>= 300 and < 400))
                return Fail("rejected", $"Login rejected (HTTP {status}). Wrong email or password, or the form uses different field names.");

            if (response.Headers.Location is not { } location)
                return Fail("no-location", $"Login answered HTTP {status} without a Location header.");

            // 3. Follow the redirect chain to confirm we are really logged in.
            var next = new Uri(loginUrl, location);
            for (int hop = 0; ; hop++)
            {
                if (IsLoginPath(next))
                    return Fail("rejected", "Login rejected: redirected back to /login. Wrong email or password?");
                if (next.AbsolutePath.Contains("two-factor", StringComparison.OrdinalIgnoreCase))
                    return Fail("two-factor", "This account requires two-factor authentication. Use a test account without it.");
                if (!SameOrigin(next, baseUri))
                    return Ok(container, $"Logged in as {email} (redirected to {next.GetLeftPart(UriPartial.Authority)}; not followed)");
                if (hop >= MaxHops)
                    return Fail("redirect-loop", "Too many redirects after login.");

                using var follow = await http.GetAsync(next, HttpCompletionOption.ResponseHeadersRead, ct);
                var followStatus = (int)follow.StatusCode;
                if (followStatus is >= 300 and < 400 && follow.Headers.Location is { } more)
                {
                    next = new Uri(next, more);
                    continue;
                }

                return Ok(container, $"Logged in as {email} (landed on {next.AbsolutePath}, HTTP {followStatus})");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("timeout", "The login timed out.");
        }
        catch (HttpRequestException ex)
        {
            return Fail("unreachable", $"Could not reach the server ({ex.Message}).");
        }
    }

    // ---------- helpers ----------

    private static TestResult Build(
        TestStatus status, Severity severity, string classification, string message, Stopwatch sw, string email)
    {
        var r = new TestResult
        {
            Category = "Authentication",
            Name = "Login",
            Status = status,
            Severity = severity,
            Duration = sw.Elapsed,
            Message = message,
            Expected = "A logged-in session for the test account"
        };
        r.Metadata["classification"] = classification;
        r.Metadata["email"] = email;
        return r;
    }

    private static string? FindToken(string html)
    {
        foreach (Match tag in InputTag.Matches(html))
            if (TokenName.IsMatch(tag.Value) && ValueAttr.Match(tag.Value) is { Success: true } v)
                return v.Groups[1].Value;

        foreach (Match tag in MetaTag.Matches(html))
            if (CsrfName.IsMatch(tag.Value) && ContentAttr.Match(tag.Value) is { Success: true } c)
                return c.Groups[1].Value;

        return null;
    }

    private static bool IsLocalHost(string host)
    {
        host = host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static bool IsLoginPath(Uri u)
    {
        var p = u.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        return p.EndsWith("/login") || p.EndsWith("/signin") || p.EndsWith("/sign-in");
    }
}
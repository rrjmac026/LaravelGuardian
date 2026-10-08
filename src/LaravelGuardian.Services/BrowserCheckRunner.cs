using System.Diagnostics;
using System.Text.RegularExpressions;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;
using Microsoft.Playwright;

namespace LaravelGuardian.Services;

/// Real-browser checks (Chromium via Playwright).
/// Safe Mode: only GET navigation; any write request a page fires on its own is blocked.
public sealed class BrowserCheckRunner : IBrowserCheckRunner
{
    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".zip", ".rar", ".csv", ".xls", ".xlsx", ".doc", ".docx", ".png", ".jpg", ".jpeg",
        ".gif", ".svg", ".webp", ".ico", ".mp3", ".mp4", ".json", ".xml", ".txt"
    };

    public async Task<IReadOnlyList<TestResult>> RunAsync(
        string baseUrl, IReadOnlyList<RouteInfo> routes, BrowserOptions options,
        AuthSession? auth = null, Guid? runId = null,
        Action<TestResult>? onResult = null, Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        var results = new List<TestResult>();
        var baseUri = new Uri(baseUrl);
        var authed = auth is { Success: true, Cookies: not null };

        var shotRoot = runId is { } id
            ? AppPaths.EvidenceDir(id)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaravelGuardian");
        var shotDir = Path.Combine(shotRoot, "screenshots");

        void Add(TestResult r)
        {
            // Tag every result of a logged-in pass with its role, so passes stay apart.
            if (authed && !string.IsNullOrWhiteSpace(options.RoleLabel))
            {
                r.Metadata["role"] = options.RoleLabel!;
                r.Name += $" [{options.RoleLabel}]";
            }
            results.Add(r);
            onResult?.Invoke(r);
        }

        IPlaywright? pw = null;
        IBrowser? browser = null;
        try
        {
            try { pw = await Playwright.CreateAsync(); }
            catch (Exception ex)
            {
                Add(EngineBlocked($"Playwright could not start: {ex.Message}"));
                return results;
            }

            var (launched, error) = await LaunchAsync(pw, options.Headless, onLog);
            if (launched is null)
            {
                Add(EngineBlocked(error ?? "Chromium could not start."));
                return results;
            }
            browser = launched;

            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new ViewportSize { Width = 1366, Height = 850 }
            });

            // Safe Mode: pages may fire POST/PUT/DELETE from JavaScript on load. Block them.
            var blockedWrites = 0;
            await context.RouteAsync("**/*", async route =>
            {
                var m = route.Request.Method;
                if (m is "GET" or "HEAD" or "OPTIONS") await route.ContinueAsync();
                else
                {
                    Interlocked.Increment(ref blockedWrites);
                    await route.AbortAsync("blockedbyclient");
                }
            });

            if (authed)
            {
                var cookies = auth!.Cookies!.GetAllCookies().Select(c => new Microsoft.Playwright.Cookie
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = baseUri.Host,
                    Path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                    HttpOnly = c.HttpOnly,
                    Secure = c.Secure
                }).ToList();
                await context.AddCookiesAsync(cookies);
            }

            // ----- crawl queue -----
            var queue = new Queue<(string Url, int Depth)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var patternCount = new Dictionary<string, int>();
            var collapsed = 0;
            string? collapsedExample = null;

            void Enqueue(string raw, int depth)
            {
                var n = Normalize(raw, baseUri, authed, options);
                if (n is null || !seen.Add(n)) return;

                var pattern = PatternOf(n);
                if (pattern.Contains("{id}"))
                {
                    patternCount.TryGetValue(pattern, out var c);
                    if (c >= options.MaxPerPattern) { collapsed++; collapsedExample ??= pattern; return; }
                    patternCount[pattern] = c + 1;
                }
                queue.Enqueue((n, depth));
            }

            Enqueue(new Uri(baseUri, "/").ToString(), 0);
            if (options.SeedFromRoutes)
            {
                var httpRules = new HttpCheckOptions();
                foreach (var route in routes)
                {
                    if (route.IsApi) continue;
                    if (HttpCheckRunner.GetSkipReason(route, httpRules, authed) is not null) continue;
                    Enqueue(new Uri(baseUri, "/" + route.Uri.TrimStart('/')).ToString(), 0);
                }
            }

            onLog?.Invoke($"Browser crawl: {queue.Count} seed page(s), limit {options.MaxPages} pages / depth {options.MaxDepth}.");

            var visited = 0;
            while (queue.Count > 0 && visited < options.MaxPages)
            {
                ct.ThrowIfCancellationRequested();
                var (url, depth) = queue.Dequeue();
                visited++;

                var (result, links) = await CheckPageAsync(context, baseUri, url, depth, authed, shotDir, options);
                Add(result);

                if (result.Status == TestStatus.Blocked) break; // server unreachable, no point continuing

                if (depth < options.MaxDepth)
                    foreach (var link in links) Enqueue(link, depth + 1);
            }

            if (queue.Count > 0 && visited >= options.MaxPages)
            {
                var limit = new TestResult
                {
                    Category = "Browser",
                    Name = "Crawl limit",
                    Status = TestStatus.Skipped,
                    Severity = Severity.Info,
                    Message = $"Page limit ({options.MaxPages}) reached; {queue.Count} discovered page(s) were not visited"
                };
                limit.Metadata["skipReason"] = "limit";
                Add(limit);
            }

            if (collapsed > 0)
            {
                var similar = new TestResult
                {
                    Category = "Browser", Name = "Similar pages", Status = TestStatus.Skipped, Severity = Severity.Info,
                    Message = $"{collapsed} page(s) skipped: {options.MaxPerPattern} sample(s) per pattern already visited (e.g. {collapsedExample})"
                };
                similar.Metadata["skipReason"] = "similar";
                Add(similar);
            }

            if (blockedWrites > 0)
            {
                var writes = new TestResult
                {
                    Category = "Browser",
                    Name = "Page-triggered writes",
                    Status = TestStatus.Skipped,
                    Severity = Severity.Info,
                    Message = $"Safe Mode blocked {blockedWrites} non-GET request(s) that pages fired on their own"
                };
                writes.Metadata["skipReason"] = "writes-blocked";
                Add(writes);
            }
        }
        finally
        {
            if (browser is not null)
            {
                try { await browser.CloseAsync(); } catch { /* already gone */ }
            }
            pw?.Dispose();
        }

        return results;
    }

    // ---------- one page ----------

    private static async Task<(TestResult Result, List<string> Links)> CheckPageAsync(
        IBrowserContext context, Uri baseUri, string url, int depth, bool authed,
        string shotDir, BrowserOptions o)
    {
        var target = new Uri(url);
        var result = new TestResult
        {
            Category = "Browser",
            Name = $"GET {target.PathAndQuery}",
            Url = url,
            Expected = "Page loads with no server error, uncaught exception or console error"
        };
        result.Metadata["depth"] = depth.ToString();
        if (authed) result.Metadata["authenticated"] = "true";

        var gate = new object();
        var consoleErrors = new List<string>();
        var network = new List<string>();
        var exceptions = new List<string>();
        var serverErrors = 0;
        var links = new List<string>();
        var sw = Stopwatch.StartNew();

        var page = await context.NewPageAsync();

        page.Console += (_, m) =>
        {
            if (m.Type != "error") return;
            // "Failed to load resource" duplicates what the network list already records.
            if (m.Text.StartsWith("Failed to load resource", StringComparison.OrdinalIgnoreCase)) return;
            lock (gate) if (consoleErrors.Count < 20) consoleErrors.Add(Shorten(m.Text, 300));
        };

        page.PageError += (_, msg) =>
        {
            lock (gate) if (exceptions.Count < 20) exceptions.Add(Shorten(msg, 300));
        };

        page.RequestFailed += (_, req) =>
        {
            var reason = req.Failure ?? "";
            if (reason.Contains("ERR_ABORTED") || req.Method != "GET" || req.Url.EndsWith("favicon.ico")) return;
            lock (gate) if (network.Count < 20) network.Add($"FAILED GET {Shorten(req.Url, 200)} ({reason})");
        };

        page.Response += (_, resp) =>
        {
            if (resp.Status < 400 || resp.Request.IsNavigationRequest || resp.Url.EndsWith("favicon.ico")) return;
            lock (gate)
            {
                if (resp.Status >= 500 && Uri.TryCreate(resp.Url, UriKind.Absolute, out var u) && SameOrigin(u, baseUri))
                    serverErrors++;
                if (network.Count < 20) network.Add($"{resp.Status} {resp.Request.Method} {Shorten(resp.Url, 200)}");
            }
        };

        TestResult Finish(TestStatus status, Severity severity, string classification, string message)
        {
            result.Status = status;
            result.Severity = severity;
            result.Message = message;
            result.Duration = sw.Elapsed;
            result.Actual = result.HttpStatus?.ToString() ?? classification;
            result.Metadata["classification"] = classification;
            lock (gate)
            {
                result.ConsoleErrors = consoleErrors.ToList();
                result.NetworkErrors = network.ToList();
                if (exceptions.Count > 0) result.Metadata["pageExceptions"] = string.Join("\n", exceptions);
            }
            return result;
        }

        async Task ShotAsync()
        {
            var cls = result.Metadata.GetValueOrDefault("classification");
            if (result.Status != TestStatus.Fail && cls is not ("console-errors" or "session-lost")) return;
            try
            {
                Directory.CreateDirectory(shotDir);
                var file = Path.Combine(shotDir, $"{result.Id:N}.png");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = file, FullPage = true });
                result.ScreenshotPath = file;
                result.Metadata["screenshot"] = file;
            }
            catch { /* a missing screenshot must not fail the check */ }
        }

        try
        {
            var response = await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout = o.NavigationTimeoutMs
            });

            // Let late XHR/fetch calls and deferred scripts settle. Pages that never go idle are fine.
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 3000 });
            }
            catch (TimeoutException) { }

            var status = response?.Status;
            result.HttpStatus = status;
            var final = new Uri(page.Url);
            var title = Shorten(await page.TitleAsync(), 100);
            if (final.PathAndQuery != target.PathAndQuery) result.Metadata["finalUrl"] = final.PathAndQuery;
            if (title.Length > 0) result.Metadata["title"] = title;

            // On a 500 page, read the exception text the app prints (same detector as the HTTP check).
            string? exType = null, exMessage = null;
            if (status >= 500)
            {
                try
                {
                    var html = await page.ContentAsync();
                    (exType, exMessage) = HttpCheckRunner.DetectException(html, "text/html");
                }
                catch { /* the title alone is still reported */ }
            }

            int jsErrors, srvErrors, conErrors, netErrors;
            string? firstJs;
            lock (gate)
            {
                jsErrors = exceptions.Count;
                firstJs = exceptions.FirstOrDefault();
                srvErrors = serverErrors;
                conErrors = consoleErrors.Count;
                netErrors = network.Count;
            }

            if (!SameOrigin(final, baseUri))
                Finish(TestStatus.Warning, Severity.Low, "redirect-external", $"Redirected to another origin ({final.Host})");
            else if (status is 401 or 403)
            {
                if (!authed)
                    Finish(TestStatus.Pass, Severity.Info, status == 401 ? "unauthorized" : "forbidden",
                        $"HTTP {status}: access denied for a guest");
                else if (status == 403)
                    Finish(TestStatus.Pass, Severity.Info, "forbidden-for-role",
                        "HTTP 403: this account's role is not allowed here (expected)");
                else
                    Finish(TestStatus.Warning, Severity.Medium, "session-lost", "HTTP 401 even with a logged-in session");
            }
            else if (status == 404)
                Finish(TestStatus.Fail, Severity.Medium, "not-found", "Page returned 404");
            else if (status >= 500)
            {
                result.ExceptionType = exType;
                result.ExceptionMessage = exMessage;

                var detail = exType is not null ? $": {exType}" : "";
                if (!string.IsNullOrWhiteSpace(exMessage))
                    detail += (exType is not null ? " - " : ": ") + Shorten(exMessage, 160);
                if (detail.Length == 0 && title.Length > 0) detail = $": {title}";

                Finish(TestStatus.Fail, Severity.High, "server-error", $"HTTP {status}{detail}");
            }
            else if (authed && IsLoginPath(final) && !IsLoginPath(target))
                Finish(TestStatus.Warning, Severity.Medium, "session-lost", "Redirected to the login page while logged in");
            else if (jsErrors > 0)
                Finish(TestStatus.Fail, Severity.Medium, "js-exception",
                    $"{jsErrors} uncaught JavaScript exception(s)" + (firstJs is null ? "" : $": {Shorten(firstJs, 200)}"));
            else if (srvErrors > 0)
                Finish(TestStatus.Fail, Severity.High, "subrequest-server-error",
                    $"{srvErrors} request(s) made by the page returned 5xx");
            else if (conErrors > 0 || netErrors > 0)
                Finish(TestStatus.Warning, Severity.Low, conErrors > 0 ? "console-errors" : "network-errors",
                    $"{conErrors} console error(s), {netErrors} failed or 4xx/5xx request(s)");
            else
                Finish(TestStatus.Pass, Severity.Info, "ok", $"HTTP {status}, no console or network errors");

            // Collect links only from healthy same-origin pages.
            if (status is >= 200 and < 300 && SameOrigin(final, baseUri)
                && result.Metadata["classification"] != "session-lost")
            {
                var hrefs = await page.EvalOnSelectorAllAsync<string[]>("a[href]", "els => els.map(e => e.href)");
                links.AddRange(hrefs);
            }

            await ShotAsync();
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("ERR_CONNECTION_REFUSED"))
        {
            Finish(TestStatus.Blocked, Severity.High, "unreachable",
                "Could not reach the server. Remaining browser checks were not run.");
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Download is starting"))
        {
            Finish(TestStatus.Skipped, Severity.Info, "download", "URL triggers a file download; not a page");
            result.Metadata["skipReason"] = "download";
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("ERR_ABORTED"))
        {
            Finish(TestStatus.Skipped, Severity.Info, "no-page", "Endpoint returns no page (204 or file), not visited as a page");
            result.Metadata["skipReason"] = "no-page";
        }
        catch (TimeoutException)
        {
            Finish(TestStatus.Fail, Severity.Medium, "timeout", $"Page did not load within {o.NavigationTimeoutMs / 1000}s");
            await ShotAsync();
        }
        catch (PlaywrightException ex)
        {
            Finish(TestStatus.Fail, Severity.Medium, "navigation-error", Shorten(ex.Message.Split('\n')[0], 200));
        }
        finally
        {
            try { await page.CloseAsync(); } catch { /* page already closed */ }
        }

        return (result, links);
    }

    // ---------- browser launch ----------

    private static async Task<(IBrowser? Browser, string? Error)> LaunchAsync(
        IPlaywright pw, bool headless, Action<string>? log)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var b = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = headless });
                return (b, null);
            }
            catch (PlaywrightException ex) when (attempt == 0 && ex.Message.Contains("Executable doesn't exist"))
            {
                log?.Invoke("Chromium is not installed yet. Installing it now (one time, ~150 MB)...");
                var code = await Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
                if (code != 0) return (null, $"Chromium install failed (exit code {code}).");
                log?.Invoke("Chromium installed.");
            }
            catch (Exception ex)
            {
                return (null, ex.Message.Split('\n')[0]);
            }
        }
        return (null, "Could not start Chromium.");
    }

    private static TestResult EngineBlocked(string message)
    {
        var r = new TestResult
        {
            Category = "Browser",
            Name = "Browser engine",
            Status = TestStatus.Blocked,
            Severity = Severity.High,
            Message = message
        };
        r.Metadata["classification"] = "engine-unavailable";
        return r;
    }

    // ---------- URL helpers ----------

    /// Returns a crawl-safe absolute URL without query or fragment, or null when it must not be visited.
    private static string? Normalize(string raw, Uri baseUri, bool authed, BrowserOptions o)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) return null;
        if (u.Scheme is not ("http" or "https")) return null;
        if (!SameOrigin(u, baseUri)) return null;

        var path = u.AbsolutePath;
        if (SkipExtensions.Contains(Path.GetExtension(path))) return null;

        var tokens = path.Split(new[] { '/', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(HttpCheckRunner.DangerousTokens.Contains)) return null;
        if (authed && tokens.Any(HttpCheckRunner.AuthDangerousTokens.Contains)) return null;

        var trimmed = path.Trim('/');
        foreach (var p in o.ExcludePatterns)
        {
            var rx = "^" + Regex.Escape(p.Trim('/')).Replace("\\*", ".*") + "$";
            if (Regex.IsMatch(trimmed, rx, RegexOptions.IgnoreCase)) return null;
        }

        // Dropping the query avoids endless ?page=N / ?sort= variations of the same page.
        var clean = u.GetLeftPart(UriPartial.Path);
        return path == "/" ? clean : clean.TrimEnd('/');
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static string PatternOf(string url) =>
    string.Join("/", new Uri(url).AbsolutePath.Split('/').Select(s =>
        s.Length > 0 && (s.All(char.IsDigit) || Guid.TryParse(s, out _)) ? "{id}" : s));

    private static bool IsLoginPath(Uri u)
    {
        var p = u.AbsolutePath.ToLowerInvariant();
        return p.Contains("login") || p.Contains("signin") || p.Contains("sign-in");
    }

    private static string Shorten(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}
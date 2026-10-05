using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LaravelGuardian.Services;

/// Reading and understanding a response: body, JSON check, exception detection, redacted evidence.
public partial class HttpCheckRunner
{
    private const int MaxBodyBytes = 1_000_000;
    private const int MaxTextChars = 200_000;

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "set-cookie", "cookie", "authorization", "proxy-authorization", "x-csrf-token", "x-xsrf-token"
    };

    private static readonly Regex ExceptionClass = new(
        @"\b(?:[A-Z][A-Za-z0-9_]*\\)+[A-Z][A-Za-z0-9_]*(?:Exception|Error)\b", RegexOptions.Compiled);
    private static readonly Regex Scripts = new(
        @"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Comments = new(
        @"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);
    // Quote-aware: a '>' inside "..." or '...' (e.g. x-data="() => x") does not end the tag.
    private static readonly Regex Tags = new(
        @"<(?:[^>""']|""[^""]*""|'[^']*')*>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Title = new(
        @"<title[^>]*>(.*?)</title>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex SqlState = new(
        @"SQLSTATE\[[^\]]+\][^(]{0,200}", RegexOptions.Compiled);
    private static readonly Regex AfterClass = new(
        @"^\s*(?:\S+\.php\s*:?\s*\d+\s*)?(.{1,200})", RegexOptions.Compiled);
    private static readonly Regex Secrets = new(
        @"(?i)((?:_token|csrf-token|password|secret|api[_-]?key|authorization|cookie)[""']?\s*(?:[:=]|content=|value=)\s*[""'])[^""']+",
        RegexOptions.Compiled);

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

    private static string Shorten(string text, int max)
    {
        text = Spaces.Replace(text, " ").Trim();
        return text.Length > max ? text[..max] + "..." : text;
    }

    /// HTML -> readable text: drops comments, scripts, styles and tags (quote-aware), decodes entities.
    private static string ToPlainText(string html)
    {
        if (html.Length > MaxTextChars) html = html[..MaxTextChars];
        html = Comments.Replace(html, " ");
        html = Scripts.Replace(html, " ");
        html = Tags.Replace(html, " ");
        html = WebUtility.HtmlDecode(html);
        return Spaces.Replace(html, " ").Trim();
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

        // Best-effort on a debug error page: first exception-looking class, plus its message.
        var plain = ToPlainText(body);
        var m = ExceptionClass.Match(plain);
        string? exType = m.Success ? m.Value : null;
        string? exMessage = null;

        var sql = SqlState.Match(plain);
        if (sql.Success)
        {
            exMessage = sql.Value.Trim();
        }
        else if (m.Success)
        {
            var rest = plain[(m.Index + m.Length)..];
            var after = AfterClass.Match(rest);
            if (after.Success) exMessage = after.Groups[1].Value.Trim();
        }

        return (exType, string.IsNullOrWhiteSpace(exMessage) ? null : exMessage);
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
            var stripped = ToPlainText(body);
            text = (title.Success ? WebUtility.HtmlDecode(title.Groups[1].Value).Trim() + " | " : "") + stripped;
        }
        else
        {
            text = body;
        }

        text = Secrets.Replace(text, "$1[redacted]");
        return text.Length > max ? text[..max] + "..." : text;
    }
}
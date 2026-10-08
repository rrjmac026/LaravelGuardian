using System.Net;
using System.Text.RegularExpressions;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

/// Parameter routes: real values come from links Guardian has already seen on fetched pages.
public partial class HttpCheckRunner
{
    private const int MaxKnownLinks = 5000;

    private static readonly Regex Href = new(
        @"<a\b[^>]*?\shref\s*=\s*(?:""([^""]*)""|'([^']*)')",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// Adds the same-origin link paths found in an HTML page (no query, no fragment).
    private static void CollectLinks(string html, Uri pageUri, Uri baseUri, ISet<string> into)
    {
        foreach (Match m in Href.Matches(html))
        {
            if (into.Count >= MaxKnownLinks) return;

            var raw = WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (raw.Length == 0 || raw[0] == '#') continue;
            if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) continue;

            if (!Uri.TryCreate(pageUri, raw, out var u)) continue;
            if (u.Scheme is not ("http" or "https") || !SameOrigin(u, baseUri)) continue;

            var path = u.AbsolutePath;
            if (path.Length > 1) path = path.TrimEnd('/');
            into.Add(path);
        }
    }

    /// Known link paths that fit the route's pattern, e.g. admin/users/{user} -> /admin/users/12.
    /// Paths that equal a static route (/admin/users/create) are never treated as a value.
    private static List<string> FindConcretePaths(
        RouteInfo route, IEnumerable<string> known, HashSet<string> staticPaths, int max)
    {
        var parts = route.Uri.Trim('/').Split('/').Select(seg =>
            Regex.Replace(seg, @"\{[^}]*\}|[^{]+",
                m => m.Value.StartsWith('{') ? "[^/]+" : Regex.Escape(m.Value)));
        var rx = new Regex("^/" + string.Join("/", parts) + "$", RegexOptions.IgnoreCase);

        return known
            .Where(p => !staticPaths.Contains(p) && rx.IsMatch(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Take(Math.Max(1, max))
            .ToList();
    }

    private static RouteInfo WithUri(RouteInfo r, string uri) => new()
    {
        Methods = r.Methods,
        Uri = uri,
        Name = r.Name,
        Action = r.Action,
        Domain = r.Domain,
        Middleware = r.Middleware
    };
}
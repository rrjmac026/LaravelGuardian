using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

public sealed class ReportExporter : IReportExporter
{
    private readonly IRunStore _store;
    public ReportExporter(IRunStore store) => _store = store;

    private static readonly Regex EmailPattern = new(
        @"(?<local>[A-Za-z0-9._%+\-]+)@(?<domain>[A-Za-z0-9.\-]+\.[A-Za-z]{2,})",
        RegexOptions.Compiled);

    public async Task<ReportPaths> ExportAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _store.GetRunAsync(runId)
                  ?? throw new InvalidOperationException("Run not found.");
        var results = await _store.GetResultsAsync(runId);

        var dir = AppPaths.RunDir(runId);
        Directory.CreateDirectory(dir);
        var htmlPath = Path.Combine(dir, "report.html");
        var jsonPath = Path.Combine(dir, "report.json");

        // Exported reports are meant to be shared: mask email addresses in both files.
        var json = MaskEmails(JsonSerializer.Serialize(new { run, results }, JsonDefaults.Pretty));
        var html = MaskEmails(BuildHtml(run, results));

        await File.WriteAllTextAsync(jsonPath, json, ct);
        await File.WriteAllTextAsync(htmlPath, html, ct);
        return new ReportPaths(htmlPath, jsonPath);
    }

    /// "erven.granada@lccdo.edu.ph" -> "er***@lccdo.edu.ph"
    private static string MaskEmails(string text) =>
        EmailPattern.Replace(text, m =>
        {
            var local = m.Groups["local"].Value;
            var visible = local.Length <= 2 ? local[..1] : local[..2];
            return $"{visible}***@{m.Groups["domain"].Value}";
        });

    private const string Css = @"
        body{font-family:Segoe UI,Arial,sans-serif;margin:0;background:#f5f6f8;color:#1c1f24}
        header{background:#1f2937;color:#fff;padding:20px 32px}
        header h1{margin:0 0 4px;font-size:22px} header div{opacity:.8;font-size:13px}
        main{padding:24px 32px;max-width:1100px}
        .cards{display:flex;gap:12px;flex-wrap:wrap;margin-bottom:24px}
        .card{background:#fff;border-radius:8px;padding:12px 18px;min-width:90px;box-shadow:0 1px 2px #0002}
        .card b{display:block;font-size:24px}
        .Fail b{color:#c0392b}.Warning b{color:#b9770e}.Pass b{color:#1e8449}.Blocked b{color:#5b4b9a}
        h2{margin:28px 0 8px;font-size:17px}
        details{background:#fff;border-radius:6px;margin:6px 0;box-shadow:0 1px 2px #0002}
        summary{padding:8px 12px;cursor:pointer}
        details>div{padding:4px 14px 12px}
        .tag{display:inline-block;min-width:62px;text-align:center;border-radius:4px;color:#fff;font-size:11px;padding:2px 6px;margin-right:8px}
        .t-Fail{background:#c0392b}.t-Warning{background:#b9770e}.t-Blocked{background:#5b4b9a}.t-Pass{background:#1e8449}
        pre{background:#f0f1f4;padding:8px;border-radius:4px;overflow:auto;white-space:pre-wrap;word-break:break-word;font-size:12px}
        table{border-collapse:collapse;width:100%;background:#fff} td,th{padding:5px 10px;border-bottom:1px solid #eee;text-align:left;font-size:13px}
        .note{background:#fff8e1;border-left:4px solid #f0b429;padding:8px 12px;font-size:13px;margin:12px 0}
        .matrix td,.matrix th{text-align:center} .matrix td:first-child,.matrix th:first-child{text-align:left}
        .matrix td.allowed{background:#d5f5e3;color:#1e8449}.matrix td.forbidden{background:#eceff1;color:#546e7a}
        .matrix td.error{background:#f9d6d5;color:#c0392b;font-weight:600}.matrix td.other{background:#fff3cd;color:#8a6d00}";

    private static string Label(string code) => code switch
    {
        "auth" => "need auth (no login used)",
        "parameters" => "have parameters",
        "method" => "non-GET (Safe Mode)",
        "excluded" => "excluded as risky",
        "domain" => "domain-bound",
        "duplicate" => "duplicates",
        "limit" => "over the route limit",
        "closure" => "closures",
        "invokable" => "invokable controllers",
        "vendor" => "vendor or framework controllers",
        _ => code
    };

    /// Route x role table: what each role got when it opened each route behind login.
    private static void AppendMatrix(StringBuilder sb, IReadOnlyList<TestResult> results)
    {
        var matrix = RoleMatrix.Build(results);
        if (matrix.IsEmpty) return;

        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

        sb.Append("<h2>Role access matrix (").Append(matrix.Rows.Count).Append(" routes, ")
          .Append(matrix.Roles.Count).Append(" role(s))</h2>");
        sb.Append("<div class=\"note\">Shows what each role was able to open, not what it should be able to open. " +
                  "Check the rows with ERROR, and the routes every role can reach.</div>");
        sb.Append("<details open><summary>Show matrix</summary><div><table class=\"matrix\"><tr><th>Route</th>");
        foreach (var role in matrix.Roles) sb.Append("<th>").Append(E(role)).Append("</th>");
        sb.Append("</tr>");

        foreach (var row in matrix.Rows)
        {
            sb.Append("<tr><td>").Append(E(row.Route)).Append("</td>");
            foreach (var role in matrix.Roles)
            {
                var cell = row.Cells.GetValueOrDefault(role);
                var text = cell switch
                {
                    RoleMatrix.Allowed => "allowed",
                    RoleMatrix.Forbidden => "forbidden",
                    RoleMatrix.Error => "ERROR",
                    RoleMatrix.Other => "other",
                    _ => "-"
                };
                sb.Append("<td class=\"").Append(cell ?? "").Append("\">").Append(text).Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table></div></details>");
    }

    private static string BuildHtml(RunSummary run, IReadOnlyList<TestResult> results)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        var duration = run.UpdatedAt - run.StartedAt;

        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Guardian – ")
          .Append(E(run.ProjectName)).Append("</title><style>").Append(Css).Append("</style></head><body>");
        sb.Append("<header><h1>Laravel Guardian report</h1><div>")
          .Append(E(run.ProjectName)).Append(" · ").Append(E(run.ProjectPath)).Append(" · ")
          .Append(run.StartedLocal).Append(" · span ").Append(duration.ToString(@"hh\:mm\:ss"))
          .Append("</div></header><main>");

        // Summary cards
        sb.Append("<div class=\"cards\">");
        void Card(string cls, string label, int n) =>
            sb.Append($"<div class=\"card {cls}\"><b>{n}</b>{label}</div>");
        Card("", "Total", run.Total);
        Card("Pass", "Passed", run.Passed);
        Card("Fail", "Failed", run.Failed);
        Card("Warning", "Warnings", run.Warnings);
        Card("Blocked", "Blocked", run.Blocked);
        Card("", "Skipped", run.Skipped);
        sb.Append("</div>");

        var loggedInResults = results.Where(r => r.Metadata.GetValueOrDefault("authenticated") == "true").ToList();
        var loggedIn = loggedInResults.Count;
        var roleCount = loggedInResults
            .Select(r => r.Metadata.GetValueOrDefault("role", ""))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var scope = loggedIn > 0
            ? $"HTTP checks are GET only (Safe Mode). {loggedIn} route check(s) were made with a logged-in test account" +
              (roleCount > 0 ? $" across {roleCount} role(s)" : "") + "; " +
              "roles that were not tested and routes that need route parameters are not covered. "
            : "HTTP checks run as a guest in Safe Mode (GET only). Routes behind authentication were not covered " +
              "because no logged-in test account was used. ";
        sb.Append("<div class=\"note\">").Append(E(scope))
          .Append("Email addresses are masked in this report. Other evidence is redacted on a best-effort basis; " +
                  "review it before sharing.</div>");

        AppendMatrix(sb, results);

        // Problems
        var problems = results
            .Where(r => r.Status is TestStatus.Fail or TestStatus.Blocked or TestStatus.Warning)
            .OrderBy(r => r.Status switch { TestStatus.Fail => 0, TestStatus.Blocked => 1, _ => 2 })
            .ThenByDescending(r => r.Severity)
            .ToList();

        sb.Append("<h2>Failures, blocked and warnings (").Append(problems.Count).Append(")</h2>");
        if (problems.Count == 0) sb.Append("<p>Nothing to report.</p>");

        foreach (var group in problems.GroupBy(r => r.Metadata.GetValueOrDefault("source", "other")))
        {
            sb.Append("<h3>").Append(E(group.Key)).Append("</h3>");
            foreach (var r in group)
            {
                sb.Append("<details><summary><span class=\"tag t-").Append(r.Status).Append("\">")
                  .Append(r.Status.ToString().ToUpper()).Append("</span>")
                  .Append(E(r.Name));
                if (r.Metadata.TryGetValue("role", out var roleLabel) && !string.IsNullOrWhiteSpace(roleLabel))
                    sb.Append(" [").Append(E(roleLabel)).Append(']');
                if (r.HttpStatus is not null) sb.Append(" · HTTP ").Append(r.HttpStatus);
                if (!string.IsNullOrWhiteSpace(r.Message)) sb.Append(" — ").Append(E(r.Message));
                sb.Append("</summary><div>");

                if (r.Url is not null) sb.Append("<p><b>URL:</b> ").Append(E(r.Url)).Append("</p>");
                if (r.Expected is not null) sb.Append("<p><b>Expected:</b> ").Append(E(r.Expected)).Append("</p>");
                if (r.Actual is not null) sb.Append("<p><b>Actual:</b> ").Append(E(r.Actual)).Append("</p>");
                if (r.ExceptionType is not null) sb.Append("<p><b>Exception:</b> ").Append(E(r.ExceptionType)).Append("</p>");
                if (!string.IsNullOrWhiteSpace(r.ExceptionMessage))
                    sb.Append("<pre>").Append(E(r.ExceptionMessage)).Append("</pre>");

                foreach (var (k, v) in r.Metadata.Where(kv => kv.Key != "source" && !string.IsNullOrWhiteSpace(kv.Value)))
                    sb.Append("<p><b>").Append(E(k)).Append(":</b></p><pre>").Append(E(v)).Append("</pre>");

                if (r.EvidencePath is not null)
                    sb.Append("<p><small>Evidence file: ").Append(E(r.EvidencePath)).Append("</small></p>");
                sb.Append("</div></details>");
            }
        }

        // Not tested
        var skipped = results.Where(r => r.Status == TestStatus.Skipped).ToList();
        sb.Append("<h2>Not tested (").Append(skipped.Count).Append(")</h2>");
        foreach (var g in skipped.GroupBy(r => r.Metadata.GetValueOrDefault("skipReason", "other"))
                                 .OrderByDescending(g => g.Count()))
        {
            sb.Append("<details><summary>").Append(g.Count()).Append(' ').Append(E(Label(g.Key)))
              .Append("</summary><div><pre>")
              .Append(E(string.Join("\n", g.Select(r => r.Name)))).Append("</pre></div></details>");
        }

        // Passed
        var passed = results.Where(r => r.Status == TestStatus.Pass).ToList();
        sb.Append("<h2>Passed (").Append(passed.Count).Append(")</h2><details><summary>Show list</summary><div><table>")
          .Append("<tr><th>Source</th><th>Name</th><th>HTTP</th><th>ms</th></tr>");
        foreach (var r in passed)
            sb.Append("<tr><td>").Append(E(r.Metadata.GetValueOrDefault("source", "")))
              .Append("</td><td>").Append(E(r.Name)).Append("</td><td>").Append(r.HttpStatus?.ToString() ?? "-")
              .Append("</td><td>").Append((int)r.Duration.TotalMilliseconds).Append("</td></tr>");
        sb.Append("</table></div></details></main></body></html>");

        return sb.ToString();
    }
}
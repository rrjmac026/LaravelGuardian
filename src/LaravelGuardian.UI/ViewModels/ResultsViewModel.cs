using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;
using LaravelGuardian.Services;

namespace LaravelGuardian.UI.ViewModels;

public sealed class ResultRow
{
    public ResultRow(TestResult r, bool hasScreenshot)
    {
        Result = r;
        HasScreenshot = hasScreenshot;
    }

    public TestResult Result { get; }
    public bool HasScreenshot { get; }
    public string Status => Result.Status.ToString();
    public string Source => Result.Metadata.GetValueOrDefault("source", "");
    public string Name => Result.Name;
    public string Http => Result.HttpStatus?.ToString() ?? "-";
    public int Ms => (int)Result.Duration.TotalMilliseconds;
    public string Shot => HasScreenshot ? "📷" : "";
    public string Message => Result.Message ?? "";
}

public partial class ResultsViewModel : ObservableObject
{
    private readonly IRunStore _store;
    private readonly IRunSession _session;
    private readonly IReportExporter _exporter;
    private List<TestResult> _all = new();

    // File name -> full path of every screenshot found in the selected run's folders.
    private readonly Dictionary<string, string> _shots = new(StringComparer.OrdinalIgnoreCase);

    public ResultsViewModel(IRunStore store, IRunSession session, IReportExporter exporter)
    {
        _store = store;
        _session = session;
        _exporter = exporter;
    }

    public ObservableCollection<RunSummary> Runs { get; } = new();
    public ObservableCollection<ResultRow> Rows { get; } = new();
    public string[] Filters { get; } =
        { "Problems", "All", "Pass", "Fail", "Warning", "Blocked", "Skipped", "With screenshot" };

    [ObservableProperty] private string _filter = "Problems";
    [ObservableProperty] private RunSummary? _selectedRun;
    [ObservableProperty] private ResultRow? _selectedRow;
    [ObservableProperty] private string _comparison = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _status = "";

    partial void OnSelectedRunChanged(RunSummary? value) => _ = LoadRunAsync(value);
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedRowChanged(ResultRow? value)
    {
        Detail = value is null ? "" : BuildDetail(value);
        OpenScreenshotCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var keep = SelectedRun?.Id ?? _session.CurrentRunId;
        var runs = await _store.GetRunsAsync();
        Runs.Clear();
        foreach (var r in runs) Runs.Add(r);
        SelectedRun = Runs.FirstOrDefault(r => r.Id == keep) ?? Runs.FirstOrDefault();
    }

    [RelayCommand]
    private void NewRun()
    {
        _session.NewRun();
        Status = "Next check you run will start a new run.";
    }

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        if (SelectedRun is null) return;
        try
        {
            var paths = await _exporter.ExportAsync(SelectedRun.Id);
            Status = $"Exported: {paths.Html}";
            Process.Start(new ProcessStartInfo(paths.Html) { UseShellExecute = true });
        }
        catch (Exception ex) { Status = $"Export failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenRunFolder()
    {
        if (SelectedRun is null) return;
        var dir = AppPaths.RunDir(SelectedRun.Id);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenEvidence()
    {
        var path = SelectedRow?.Result.EvidencePath;
        if (path is null || !File.Exists(path)) { Status = "No evidence file for this result."; return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private bool CanOpenScreenshot() => SelectedRow?.HasScreenshot == true;

    /// Enabled only when the selected row has a 📷. Double-clicking a row does the same.
    [RelayCommand(CanExecute = nameof(CanOpenScreenshot))]
    private void OpenScreenshot()
    {
        if (SelectedRow is null) return;

        var tried = new List<string>();
        var path = FindScreenshot(SelectedRow.Result, tried);
        if (path is null)
        {
            Status = "Screenshot file not found (see the lookup list in the detail panel).";
            Detail = BuildDetail(SelectedRow) + Environment.NewLine +
                     "[screenshot lookup]" + Environment.NewLine +
                     (tried.Count == 0 ? "No screenshot path is stored on this result." : string.Join(Environment.NewLine, tried));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Status = $"Opened screenshot: {path}";
        }
        catch (Exception ex) { Status = $"Could not open screenshot: {ex.Message}"; }
    }

    private async Task LoadRunAsync(RunSummary? run)
    {
        Rows.Clear();
        Detail = "";
        Comparison = "";
        _all = new List<TestResult>();
        _shots.Clear();
        if (run is null) return;

        _all = (await _store.GetResultsAsync(run.Id)).ToList();
        IndexScreenshots(run);
        ApplyFilter();

        var previous = Runs
            .Where(r => r.ProjectPath.Equals(run.ProjectPath, StringComparison.OrdinalIgnoreCase)
                        && r.StartedAt < run.StartedAt)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefault();

        if (previous is null) { Comparison = "No earlier run of this project to compare with."; return; }

        var prevResults = await _store.GetResultsAsync(previous.Id);
        var cmp = RunComparer.Compare(prevResults, _all);
        var sb = new StringBuilder();
        sb.Append($"vs run of {previous.StartedLocal}: {cmp.NewFailures.Count} new failures, ")
          .Append($"{cmp.StillFailing.Count} still failing, {cmp.Fixed.Count} fixed.");
        if (cmp.NewFailures.Count > 0)
            sb.Append("\nNew: ").Append(string.Join(", ", cmp.NewFailures.Take(8)))
              .Append(cmp.NewFailures.Count > 8 ? ", ..." : "");
        if (cmp.Fixed.Count > 0)
            sb.Append("\nFixed: ").Append(string.Join(", ", cmp.Fixed.Take(8)))
              .Append(cmp.Fixed.Count > 8 ? ", ..." : "");
        Comparison = sb.ToString();
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        IEnumerable<TestResult> q = Filter switch
        {
            "Problems" => _all.Where(r => r.Status is TestStatus.Fail or TestStatus.Warning or TestStatus.Blocked),
            "All" => _all,
            "With screenshot" => _all.Where(HasScreenshot),
            _ => _all.Where(r => r.Status.ToString() == Filter)
        };

        // Failures first; OrderBy is stable, so the original order is kept inside each group.
        foreach (var r in q.OrderBy(r => Rank(r.Status))) Rows.Add(new ResultRow(r, HasScreenshot(r)));
    }

    private static int Rank(TestStatus s) => s switch
    {
        TestStatus.Fail => 0,
        TestStatus.Blocked => 1,
        TestStatus.Warning => 2,
        TestStatus.Skipped => 3,
        _ => 4
    };

    // ---------- screenshots ----------

    /// Lists every .png under the run's folders once, so each row can be checked cheaply.
    private void IndexScreenshots(RunSummary run)
    {
        foreach (var dir in new[] { AppPaths.EvidenceDir(run.Id), AppPaths.RunDir(run.Id) }.Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories))
                    _shots.TryAdd(Path.GetFileName(file), file);
            }
            catch { /* folder not readable */ }
        }
    }

    /// True when a screenshot file for this result exists on disk.
    private bool HasScreenshot(TestResult r)
    {
        if (!string.IsNullOrWhiteSpace(r.ScreenshotPath) && File.Exists(r.ScreenshotPath)) return true;

        foreach (var (key, value) in r.Metadata)
            if (key.Contains("screenshot", StringComparison.OrdinalIgnoreCase) && File.Exists(value))
                return true;

        return _shots.ContainsKey($"{r.Id:N}.png");
    }

    /// Looks for the screenshot file: the result's ScreenshotPath, any metadata entry with
    /// "screenshot" in its key, any .png path in the evidence JSON, then the run's folder
    /// index (files are named after the result id). Every path tried goes into `tried`.
    private string? FindScreenshot(TestResult r, List<string>? tried = null)
    {
        var candidates = new List<string?> { r.ScreenshotPath };
        foreach (var (key, value) in r.Metadata)
            if (key.Contains("screenshot", StringComparison.OrdinalIgnoreCase))
                candidates.Add(value);

        if (!string.IsNullOrEmpty(r.EvidencePath) && File.Exists(r.EvidencePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(r.EvidencePath));
                foreach (var s in StringValues(doc.RootElement))
                    if (s.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) candidates.Add(s);
            }
            catch { /* unreadable evidence file */ }
        }

        var dirs = new List<string>();
        if (SelectedRun is not null)
        {
            dirs.Add(AppPaths.EvidenceDir(SelectedRun.Id));
            dirs.Add(AppPaths.RunDir(SelectedRun.Id));
        }

        foreach (var candidate in candidates)
        {
            var path = candidate?.Trim();
            if (string.IsNullOrEmpty(path)) continue;

            tried?.Add(path);
            if (File.Exists(path)) return path;

            foreach (var dir in dirs)
            {
                var combined = Path.Combine(dir, path);
                if (File.Exists(combined)) return combined;
            }
        }

        var fileName = $"{r.Id:N}.png";
        tried?.Add($"run folder index: {fileName} ({_shots.Count} screenshot file(s) in the run folders)");
        return _shots.TryGetValue(fileName, out var indexed) ? indexed : null;
    }

    private static IEnumerable<string> StringValues(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                yield return e.GetString() ?? "";
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    foreach (var s in StringValues(item)) yield return s;
                break;
            case JsonValueKind.Object:
                foreach (var prop in e.EnumerateObject())
                    foreach (var s in StringValues(prop.Value)) yield return s;
                break;
        }
    }

    private string BuildDetail(ResultRow row)
    {
        var r = row.Result;
        var sb = new StringBuilder();
        sb.AppendLine($"{r.Status.ToString().ToUpper()}  {r.Name}   (severity {r.Severity})");
        if (row.HasScreenshot) sb.AppendLine("📷 Screenshot available: double-click this row or press Open screenshot");
        if (r.Url is not null) sb.AppendLine($"URL: {r.Url}");
        if (r.HttpStatus is not null) sb.AppendLine($"HTTP: {r.HttpStatus}");
        if (!string.IsNullOrWhiteSpace(r.Message)) sb.AppendLine($"Message: {r.Message}");
        if (r.Expected is not null) sb.AppendLine($"Expected: {r.Expected}");
        if (r.Actual is not null) sb.AppendLine($"Actual: {r.Actual}");
        if (r.ExceptionType is not null) sb.AppendLine($"Exception: {r.ExceptionType}");
        if (!string.IsNullOrWhiteSpace(r.ExceptionMessage)) sb.AppendLine().AppendLine(r.ExceptionMessage);
        foreach (var (k, v) in r.Metadata.Where(kv => kv.Key != "source" && !string.IsNullOrWhiteSpace(kv.Value)))
            sb.AppendLine().AppendLine($"[{k}]").AppendLine(v);
        if (r.EvidencePath is not null) sb.AppendLine().AppendLine($"Evidence: {r.EvidencePath}");
        return sb.ToString();
    }
}
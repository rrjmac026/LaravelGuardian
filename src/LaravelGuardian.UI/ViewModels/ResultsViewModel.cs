using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;
using LaravelGuardian.Services;

namespace LaravelGuardian.UI.ViewModels;

public sealed class ResultRow
{
    public ResultRow(TestResult r) => Result = r;
    public TestResult Result { get; }
    public string Status => Result.Status.ToString();
    public string Source => Result.Metadata.GetValueOrDefault("source", "");
    public string Name => Result.Name;
    public string Http => Result.HttpStatus?.ToString() ?? "-";
    public int Ms => (int)Result.Duration.TotalMilliseconds;
    public string Message => Result.Message ?? "";
}

public partial class ResultsViewModel : ObservableObject
{
    private readonly IRunStore _store;
    private readonly IRunSession _session;
    private readonly IReportExporter _exporter;
    private List<TestResult> _all = new();

    public ResultsViewModel(IRunStore store, IRunSession session, IReportExporter exporter)
    {
        _store = store;
        _session = session;
        _exporter = exporter;
    }

    public ObservableCollection<RunSummary> Runs { get; } = new();
    public ObservableCollection<ResultRow> Rows { get; } = new();
    public string[] Filters { get; } = { "Problems", "All", "Pass", "Fail", "Warning", "Blocked", "Skipped" };

    [ObservableProperty] private string _filter = "Problems";
    [ObservableProperty] private RunSummary? _selectedRun;
    [ObservableProperty] private ResultRow? _selectedRow;
    [ObservableProperty] private string _comparison = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _status = "";

    partial void OnSelectedRunChanged(RunSummary? value) => _ = LoadRunAsync(value);
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedRowChanged(ResultRow? value) =>
        Detail = value is null ? "" : BuildDetail(value.Result);

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

    private async Task LoadRunAsync(RunSummary? run)
    {
        Rows.Clear();
        Detail = "";
        Comparison = "";
        _all = new List<TestResult>();
        if (run is null) return;

        _all = (await _store.GetResultsAsync(run.Id)).ToList();
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
            _ => _all.Where(r => r.Status.ToString() == Filter)
        };
        foreach (var r in q) Rows.Add(new ResultRow(r));
    }

    private static string BuildDetail(TestResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{r.Status.ToString().ToUpper()}  {r.Name}   (severity {r.Severity})");
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
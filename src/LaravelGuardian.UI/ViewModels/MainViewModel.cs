using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;
using Microsoft.Win32;
using Microsoft.Extensions.DependencyInjection;

namespace LaravelGuardian.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IProjectScanner _scanner;
    private readonly IToolDetector _tools;
    private readonly IEnvironmentManager _env;
    private readonly INativeTestRunner _tests;
    private readonly IRouteScanner _routes;
    private readonly IHttpCheckRunner _http;
    private CancellationTokenSource? _runCts;
    private readonly IRunSession _session;
    private readonly IServiceProvider _services;
    private ResultsWindow? _resultsWindow;

    public MainViewModel(
        IProjectScanner scanner, IToolDetector tools, IEnvironmentManager env,
        IProcessManager processes, INativeTestRunner tests, IRouteScanner routes,
        IHttpCheckRunner http, IRunSession session, IServiceProvider services)
    {
        _session = session;
        _services = services;
        _scanner = scanner;
        _tools = tools;
        _env = env;
        _tests = tests;
        _routes = routes;
        _http = http;

        processes.OutputReceived += (name, line, isError) =>
            Log($"[{name}] {(isError ? "ERR " : "")}{line}");

        processes.ProcessExited += (name, code) =>
        {
            Log($"[{name}] exited (code {code})");
            if (name == "laravel") OnUi(() => IsEnvironmentRunning = false);
        };
    }

    [ObservableProperty] private string _projectPath = "";
    [ObservableProperty] private string _summary = "No project selected";
    [ObservableProperty] private string _baseUrl = "";
    [ObservableProperty] private bool _allowSharedDatabase;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartEnvironmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunTestsCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverRoutesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunHttpChecksCommand))]
    private ProjectInfo? _project;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartEnvironmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopEnvironmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunTestsCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscoverRoutesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunHttpChecksCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartEnvironmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopEnvironmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunHttpChecksCommand))]
    private bool _isEnvironmentRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    private bool _isRunningChecks;

    public ObservableCollection<ToolInfo> DetectedTools { get; } = new();
    public ObservableCollection<string> Activity { get; } = new();

    public List<RouteInfo> DiscoveredRoutes { get; private set; } = new();
    public List<TestResult> LastHttpResults { get; private set; } = new();

    // ---------- Project selection ----------

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Select Laravel project root" };
        if (dialog.ShowDialog() == true)
        {
            ProjectPath = dialog.FolderName;
            await ScanAsync();
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath)) return;
        IsBusy = true;
        try
        {
            Log($"Scanning {ProjectPath}");
            Project = await _scanner.ScanAsync(ProjectPath);
            DiscoveredRoutes = new List<RouteInfo>();

            if (!Project.IsLaravel)
            {
                Summary = $"Not a valid Laravel project. Missing: {string.Join(", ", Project.MissingItems)}";
                Log(Summary);
                return;
            }

            var parts = new List<string> { $"Laravel {Project.LaravelVersion}" };
            if (Project.FrontendFramework is not null) parts.Add(Project.FrontendFramework);
            if (Project.UsesVite) parts.Add("Vite");
            parts.Add(Project.HasPest ? "Pest" : Project.HasPhpUnit ? "PHPUnit" : "No test framework");
            Summary = string.Join(" • ", parts);
            Log($"Laravel detected: {Project.LaravelVersion}");

            DetectedTools.Clear();
            var checks = await Task.WhenAll(
                _tools.DetectAsync("PHP", "php", "-v"),
                _tools.DetectAsync("Composer", "composer", "--version"),
                _tools.DetectAsync("Node", "node", "-v"),
                _tools.DetectAsync("NPM", "npm", "-v"));
            foreach (var t in checks)
            {
                DetectedTools.Add(t);
                Log($"{t.Name}: {(t.Found ? t.Version : "NOT FOUND")}");
            }
        }
        catch (Exception ex)
        {
            Log($"Scan failed: {ex.Message}");
            Serilog.Log.Error(ex, "Project scan failed");
        }
        finally { IsBusy = false; }
    }

    // ---------- Environment ----------

    private bool CanStart() => Project?.IsLaravel == true && !IsEnvironmentRunning && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartEnvironmentAsync()
    {
        if (Project is null) return;
        IsBusy = true;
        try
        {
            Log("Starting environment...");
            var results = await _env.StartAsync(Project, startVite: Project.UsesVite);
            foreach (var r in results)
                Log($"{r.Status.ToString().ToUpper(),-8} {r.Name}: {r.Message}");

            BaseUrl = _env.BaseUrl ?? "";
            IsEnvironmentRunning = _env.IsRunning;

            await SaveAsync("environment", results);
        }
        catch (Exception ex)
        {
            Log($"Start failed: {ex.Message}");
            Serilog.Log.Error(ex, "Environment start failed");
        }
        finally { IsBusy = false; }
    }

    private bool CanStop() => IsEnvironmentRunning && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopEnvironmentAsync()
    {
        IsBusy = true;
        try
        {
            await _env.StopAsync();
            BaseUrl = "";
            IsEnvironmentRunning = false;
            Log("Environment stopped");
        }
        finally { IsBusy = false; }
    }

    // ---------- Laravel engine ----------

    private bool CanRunChecks() => Project?.IsLaravel == true && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunChecks))]
    private async Task RunTestsAsync()
    {
        var project = Project;
        if (project is null) return;

        await RunCancellableAsync(async ct =>
        {
            Log("Running native tests (php artisan test)...");
            var results = await _tests.RunAsync(project, AllowSharedDatabase, ct);
            await SaveAsync("tests", results);
            LogTestSummary(results);
        });
    }

    [RelayCommand(CanExecute = nameof(CanRunChecks))]
    private async Task DiscoverRoutesAsync()
    {
        var project = Project;
        if (project is null) return;

        await RunCancellableAsync(async ct =>
        {
            Log("Discovering routes (php artisan route:list)...");
            await DiscoverRoutesCoreAsync(project, ct);
        });
    }

    private async Task<bool> DiscoverRoutesCoreAsync(ProjectInfo project, CancellationToken ct)
    {
        var scan = await _routes.ScanAsync(project, ct);
        Log($"{scan.Result.Status.ToString().ToUpper(),-8} {scan.Result.Name}: {scan.Result.Message}");
        await SaveAsync("routes", new[] { scan.Result });

        if (scan.Result.Status != TestStatus.Pass)
        {
            if (!string.IsNullOrWhiteSpace(scan.Result.ExceptionMessage))
                Log(scan.Result.ExceptionMessage!);
            return false;
        }

        DiscoveredRoutes = scan.Routes;
        var r = scan.Routes;
        Log($"  GET routes: {r.Count(x => x.IsGet)}  |  API: {r.Count(x => x.IsApi)}  |  " +
            $"need auth: {r.Count(x => x.RequiresAuth)}  |  with parameters: {r.Count(x => x.HasParameters)}");
        Log($"  Safe to health-check (GET, no parameters, no auth): " +
            $"{r.Count(x => x.IsGet && !x.HasParameters && !x.RequiresAuth)}");
        return true;
    }

    // ---------- HTTP engine ----------

    private bool CanRunHttp() => Project?.IsLaravel == true && IsEnvironmentRunning && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunHttp))]
    private async Task RunHttpChecksAsync()
    {
        var project = Project;
        var baseUrl = _env.BaseUrl;
        if (project is null || string.IsNullOrEmpty(baseUrl)) return;

        await RunCancellableAsync(async ct =>
        {
            if (DiscoveredRoutes.Count == 0)
            {
                Log("No routes yet, discovering them first...");
                if (!await DiscoverRoutesCoreAsync(project, ct)) return;
            }

            Log($"Running HTTP checks against {baseUrl} (GET only, one request at a time)...");

            var results = await _http.RunAsync(baseUrl, DiscoveredRoutes, new HttpCheckOptions(), r =>
            {
                if (r.Status == TestStatus.Skipped) return;
                if (r.Metadata.GetValueOrDefault("classification") == "aborted") return; // summarized at the end

                Log($"  {r.Status.ToString().ToUpper(),-8} {r.Name}  " +
                    $"[{r.HttpStatus?.ToString() ?? "-"}]  {r.Duration.TotalMilliseconds:0} ms  {r.Message}");

                // The message already carries the detected exception detail; show the raw
                // snippet only when nothing readable was detected.
                if (r.Status == TestStatus.Fail
                    && string.IsNullOrWhiteSpace(r.ExceptionMessage)
                    && r.Metadata.TryGetValue("bodySnippet", out var snippet)
                    && !string.IsNullOrWhiteSpace(snippet))
                    Log("      " + (snippet.Length > 300 ? snippet[..300] + "..." : snippet));
            }, ct);

            LastHttpResults = results.ToList();
            await SaveAsync("http", LastHttpResults);
            LogHttpSummary(results);
        });
    }

    private void LogHttpSummary(IReadOnlyList<TestResult> results)
    {
        static bool IsAborted(TestResult r) => r.Metadata.GetValueOrDefault("classification") == "aborted";

        var tested = results.Where(r => r.Status != TestStatus.Skipped && !IsAborted(r)).ToList();
        var notRun = results.Where(IsAborted).ToList();
        int Count(TestStatus s) => tested.Count(r => r.Status == s);

        Log($"HTTP checks: {tested.Count} checked | {Count(TestStatus.Pass)} passed | " +
            $"{Count(TestStatus.Fail)} failed | {Count(TestStatus.Warning)} warnings | " +
            $"{Count(TestStatus.Blocked)} blocked");

        var byClass = tested
            .GroupBy(r => r.Metadata.GetValueOrDefault("classification", "other"))
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}");
        if (tested.Count > 0) Log("  Breakdown: " + string.Join(", ", byClass));

        if (notRun.Count > 0)
            Log($"  Stopped early, {notRun.Count} more route(s) not run. {notRun[0].Message}");

        static string Label(string code) => code switch
        {
            "auth" => "need auth",
            "parameters" => "have parameters",
            "method" => "non-GET (Safe Mode)",
            "excluded" => "excluded as risky",
            "domain" => "domain-bound",
            "duplicate" => "duplicates",
            "limit" => "over the route limit",
            _ => code
        };

        var skipped = results.Where(r => r.Status == TestStatus.Skipped)
            .GroupBy(r => r.Metadata.GetValueOrDefault("skipReason", "other"))
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {Label(g.Key)}");
        var skippedCount = results.Count(r => r.Status == TestStatus.Skipped);
        if (skippedCount > 0) Log($"  Not tested ({skippedCount}): " + string.Join(", ", skipped));
    }

    // ---------- Run plumbing ----------

    [RelayCommand(CanExecute = nameof(IsRunningChecks))]
    private void CancelRun()
    {
        Log("Cancelling...");
        _runCts?.Cancel();
    }

    private async Task RunCancellableAsync(Func<CancellationToken, Task> work)
    {
        IsBusy = true;
        IsRunningChecks = true;
        _runCts = new CancellationTokenSource();
        try
        {
            await work(_runCts.Token);
        }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception ex)
        {
            Log($"Run failed: {ex.Message}");
            Serilog.Log.Error(ex, "Run failed");
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            IsRunningChecks = false;
            IsBusy = false;
        }
    }

    private void LogTestSummary(IReadOnlyList<TestResult> results)
    {
        int Count(TestStatus s) => results.Count(r => r.Status == s);

        Log($"Native tests: {results.Count} total | {Count(TestStatus.Pass)} passed | " +
            $"{Count(TestStatus.Fail)} failed | {Count(TestStatus.Warning)} warnings | " +
            $"{Count(TestStatus.Skipped)} skipped | {Count(TestStatus.Blocked)} blocked");

        var problems = results.Where(r => r.Status is TestStatus.Fail or TestStatus.Blocked).ToList();
        foreach (var r in problems.Take(20))
            Log($"  {r.Status.ToString().ToUpper(),-8} {r.Name}: {r.Message}");
        if (problems.Count > 20) Log($"  ...and {problems.Count - 20} more");
    }

    // ---------- Helpers ----------

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        OnUi(() =>
        {
            Activity.Add(line);
            if (Activity.Count > 1000) Activity.RemoveAt(0);
        });
    }

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
    private async Task SaveAsync(string source, IEnumerable<TestResult> results)
    {
        try
        {
            await _session.RecordAsync(source, ProjectPath, results.ToList());
        }
        catch (Exception ex)
        {
            Log($"Could not save {source} results: {ex.Message}");
            Serilog.Log.Error(ex, "Saving {Source} results failed", source);
        }
    }

    [RelayCommand]
    private void OpenResults()
    {
        if (_resultsWindow is { IsLoaded: true })
        {
            _resultsWindow.Activate();
            _ = ((ViewModels.ResultsViewModel)_resultsWindow.DataContext).RefreshCommand.ExecuteAsync(null);
            return;
        }
        _resultsWindow = _services.GetRequiredService<ResultsWindow>();
        _resultsWindow.Closed += (_, _) => _resultsWindow = null;
        _resultsWindow.Show();
    }
}
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;
using Microsoft.Win32;
using Microsoft.Extensions.DependencyInjection;

namespace LaravelGuardian.UI.ViewModels;

/// One row of the TEST ACCOUNTS list: a role label plus the login used for that role.
public partial class RoleAccount : ObservableObject
{
    [ObservableProperty] private string _role = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _use;
}

public partial class MainViewModel : ObservableObject
{
    private readonly IProjectScanner _scanner;
    private readonly IToolDetector _tools;
    private readonly IEnvironmentManager _env;
    private readonly INativeTestRunner _tests;
    private readonly IRouteScanner _routes;
    private readonly IHttpCheckRunner _http;
    private readonly ISeederScanner _seeders;
    private readonly ISecretStore _secrets;
    private readonly IAuthLogin _login;
    private CancellationTokenSource? _runCts;
    private readonly IRunSession _session;
    private readonly IServiceProvider _services;
    private ResultsWindow? _resultsWindow;

    public MainViewModel(
        IProjectScanner scanner, IToolDetector tools, IEnvironmentManager env,
        IProcessManager processes, INativeTestRunner tests, IRouteScanner routes,
        IHttpCheckRunner http, IRunSession session, IServiceProvider services,
        ISeederScanner seeders, ISecretStore secrets, IAuthLogin login)
    {
        _session = session;
        _services = services;
        _scanner = scanner;
        _tools = tools;
        _env = env;
        _tests = tests;
        _routes = routes;
        _http = http;
        _seeders = seeders;
        _secrets = secrets;
        _login = login;

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

    // Remember the accounts that logged in successfully (passwords are DPAPI-encrypted)
    [ObservableProperty] private bool _rememberAccount = true;

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
    public ObservableCollection<RoleAccount> TestAccounts { get; } = new();

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

            TestAccounts.Clear();

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

            await LoadAccountsAsync();
        }
        catch (Exception ex)
        {
            Log($"Scan failed: {ex.Message}");
            Serilog.Log.Error(ex, "Project scan failed");
        }
        finally { IsBusy = false; }
    }

    // ---------- Test accounts (one row per role) ----------

    private async Task LoadAccountsAsync()
    {
        try
        {
            var saved = _secrets.LoadAll(ProjectPath);
            var found = await _seeders.ScanAsync(ProjectPath);

            Log(found.Count > 0
                ? $"Seeder scan: {found.Count} account(s) found: {string.Join(", ", found.Select(a => a.Display))}"
                : "Seeder scan: no accounts found in database/seeders");

            TestAccounts.Clear();

            // One row per role from the seeders; prefer an account whose password is known.
            // Accounts without a role get a row with an empty role, unticked.
            foreach (var group in found.GroupBy(
                         a => string.IsNullOrWhiteSpace(a.Role) ? "" : a.Role!.Trim(),
                         StringComparer.OrdinalIgnoreCase))
            {
                var pick = group.OrderByDescending(a => a.Password is not null).First();
                TestAccounts.Add(new RoleAccount
                {
                    Role = group.Key,
                    Email = pick.Email,
                    Password = pick.Password ?? "",
                    Use = group.Key.Length > 0
                });
            }

            // Remembered accounts win over the seeder suggestion for the same email or role.
            foreach (var s in saved)
            {
                var row = TestAccounts.FirstOrDefault(a => a.Email.Equals(s.Email, StringComparison.OrdinalIgnoreCase))
                          ?? (string.IsNullOrWhiteSpace(s.Role)
                              ? null
                              : TestAccounts.FirstOrDefault(a => a.Role.Equals(s.Role, StringComparison.OrdinalIgnoreCase)));

                if (row is null)
                {
                    TestAccounts.Add(new RoleAccount
                    {
                        Role = s.Role ?? "",
                        Email = s.Email,
                        Password = s.Password ?? "",
                        Use = true
                    });
                    continue;
                }

                row.Email = s.Email;
                if (!string.IsNullOrWhiteSpace(s.Role)) row.Role = s.Role!;
                if (!string.IsNullOrEmpty(s.Password)) row.Password = s.Password!;
                row.Use = true;
            }

            if (TestAccounts.Count == 0)
            {
                Log("No test accounts yet. Use + Add account to enter one.");
                return;
            }

            Log($"Test accounts: {string.Join(", ", TestAccounts.Select(a => string.IsNullOrWhiteSpace(a.Role) ? a.Email : a.Role))}");
            var noPassword = TestAccounts.Where(a => a.Use && string.IsNullOrEmpty(a.Password)).ToList();
            if (noPassword.Count > 0)
                Log($"  Password not found for: {string.Join(", ", noPassword.Select(RoleLabel))}. Type it in the account list.");
        }
        catch (Exception ex)
        {
            Log($"Account lookup failed: {ex.Message}");
            Serilog.Log.Error(ex, "Account lookup failed");
        }
    }

    [RelayCommand]
    private void AddAccount() => TestAccounts.Add(new RoleAccount { Use = true });

    [RelayCommand]
    private void RemoveAccount(RoleAccount? account)
    {
        if (account is null) return;
        TestAccounts.Remove(account);

        var email = account.Email.Trim();
        if (email.Length == 0 || string.IsNullOrWhiteSpace(ProjectPath)) return;
        try { _secrets.Delete(ProjectPath, email); }
        catch (Exception ex) { Log($"Could not remove the saved account: {ex.Message}"); }
    }

    [RelayCommand]
    private void ForgetAccount()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath)) return;
        try { _secrets.Delete(ProjectPath); }
        catch (Exception ex) { Log($"Could not remove the saved accounts: {ex.Message}"); return; }

        foreach (var a in TestAccounts) a.Password = "";
        Log("Saved test accounts removed for this project (passwords cleared from the list).");
    }

    private static string RoleLabel(RoleAccount a) =>
        string.IsNullOrWhiteSpace(a.Role) ? a.Email.Trim() : a.Role.Trim();

    private List<RoleAccount> UsableAccounts() =>
        TestAccounts.Where(a => a.Use && a.Email.Trim().Length > 0 && !string.IsNullOrEmpty(a.Password)).ToList();

    /// The account the browser crawl logs in with until it gets its own per-role pass: admin first.
    private RoleAccount? PrimaryAccount()
    {
        var rows = UsableAccounts();
        return rows.FirstOrDefault(a => a.Role.Trim().Equals("admin", StringComparison.OrdinalIgnoreCase))
               ?? rows.FirstOrDefault();
    }

    private const int MaxFailedLogins = 3;

    private static readonly HashSet<string> SystemicLoginFailures = new(StringComparer.OrdinalIgnoreCase)
    {
        "bad-url", "not-local", "login-page", "no-csrf", "csrf-expired",
        "throttled", "unreachable", "timeout", "redirect-loop", "no-location"
    };


    /// One login per ticked role (the only POSTs Guardian sends). Returns the sessions that worked.
    private async Task<List<AuthSession>> LoginAllRolesAsync(string baseUrl, CancellationToken ct)
    {
        var sessions = new List<AuthSession>();
        var rows = UsableAccounts();
        if (rows.Count == 0)
        {
            Log("No test account ticked (with email and password), so routes that need login will not be tested.");
            return sessions;
        }

        var loginResults = new List<TestResult>();
        var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = 0;
        string? stopReason = null;
        var stoppedAt = rows.Count;

        for (int i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var row = rows[i];
            var email = row.Email.Trim();
            var label = RoleLabel(row);
            if (!usedLabels.Add(label)) { label = $"{label} ({email})"; usedLabels.Add(label); }

            Log($"Logging in as {email} [{label}] (the only POST Guardian sends)...");
            var attempt = await _login.LoginAsync(baseUrl, email, row.Password, ct);
            attempt.Role = label;
            attempt.Result.Name = $"Login ({label})";
            attempt.Result.Metadata["role"] = label;
            loginResults.Add(attempt.Result);

            Log($"  {attempt.Result.Status.ToString().ToUpper(),-8} {attempt.Result.Name}: {attempt.Message}");

            if (attempt.Success)
            {
                sessions.Add(attempt);
                if (RememberAccount)
                {
                    try { _secrets.Save(ProjectPath, email, row.Role, row.Password); }
                    catch (Exception ex) { Log($"Could not save the account: {ex.Message}"); }
                }
                continue;
            }

            var cls = attempt.Result.Metadata.GetValueOrDefault("classification", "");
            if (SystemicLoginFailures.Contains(cls))
            {
                stopReason = $"The login problem is not about this account ({cls}), so other accounts would fail the same way.";
                stoppedAt = i + 1;
                break;
            }

            failures++;
            Log($"  Skipping role {label}. ({failures} of {MaxFailedLogins} failed logins allowed)");
            if (failures >= MaxFailedLogins)
            {
                stopReason = $"{failures} logins failed. Guardian stops here to avoid locking accounts or hitting rate limits.";
                stoppedAt = i + 1;
                break;
            }
        }

        if (stopReason is not null)
        {
            var untried = rows.Skip(stoppedAt).Select(RoleLabel).ToList();
            Log($"Stopped trying logins. {stopReason}");
            if (untried.Count > 0) Log($"  Not tried: {string.Join(", ", untried)}");
            Log(sessions.Count == 0
                ? "  No login worked. The seeder passwords are often not the real ones. Type the correct email/password " +
                "in the TEST ACCOUNTS list (or use + Add account), then run again. Continuing with guest checks only."
                : $"  Continuing with the {sessions.Count} role(s) that did log in. Fix or add the others in TEST ACCOUNTS and run again.");
        }

        await SaveAsync("auth", loginResults);
        return sessions;
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

            // One login per ticked role, then GET-only checks with each session.
            var sessions = await LoginAllRolesAsync(baseUrl, ct);

            Log($"Running HTTP checks against {baseUrl} (GET only, one request at a time)...");

            void Show(TestResult r)
            {
                if (r.Status == TestStatus.Skipped) return;
                if (r.Metadata.GetValueOrDefault("classification") == "aborted") return; // summarized at the end

                var role = r.Metadata.TryGetValue("role", out var rl) && !string.IsNullOrWhiteSpace(rl) ? $" [{rl}]" : "";
                Log($"  {r.Status.ToString().ToUpper(),-8} {r.Name}{role}  " +
                    $"[{r.HttpStatus?.ToString() ?? "-"}]  {r.Duration.TotalMilliseconds:0} ms  {r.Message}");

                // The message already carries the detected exception detail; show the raw
                // snippet only when nothing readable was detected.
                if (r.Status == TestStatus.Fail
                    && string.IsNullOrWhiteSpace(r.ExceptionMessage)
                    && r.Metadata.TryGetValue("bodySnippet", out var snippet)
                    && !string.IsNullOrWhiteSpace(snippet))
                    Log("      " + (snippet.Length > 300 ? snippet[..300] + "..." : snippet));
            }

            var all = new List<TestResult>();
            var knownLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (sessions.Count == 0)
            {
                all.AddRange(await _http.RunAsync(baseUrl, DiscoveredRoutes,
                    new HttpCheckOptions { KnownLinks = knownLinks }, null, Show, ct));
            }
            else
            {
                // First pass: guests + the first role. Later passes: only the routes behind login.
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    Log($"--- Role: {s.Role} ---");
                    var options = new HttpCheckOptions
                    {
                        RoleLabel = s.Role,
                        AuthenticatedRoutesOnly = i > 0,
                        KnownLinks = knownLinks
                    };
                    all.AddRange(await _http.RunAsync(baseUrl, DiscoveredRoutes, options, s, Show, ct));
                }

                if (sessions.Count >= 2)
                {
                    var matrix = RoleMatrix.Build(all);
                    var issues = matrix.FindIssues();
                    LogRoleMatrix(matrix, issues);
                    all.AddRange(issues);
                }
            }
            if (sessions.Count < 2)
                {
                    var fixedPaths = DiscoveredRoutes
                        .Where(r => !r.HasParameters)
                        .Select(r => "/" + r.Uri.TrimStart('/'))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var idLike = knownLinks.Where(p => !fixedPaths.Contains(p)).OrderBy(p => p).ToList();
                    Log($"Links seen on pages: {knownLinks.Count} total, {idLike.Count} not matching a fixed route");
                    foreach (var p in idLike.Take(30)) Log($"    {p}");
                    if (idLike.Count > 30) Log($"    ...and {idLike.Count - 30} more");

                    var noValue = all
                        .Where(r => r.Status == TestStatus.Skipped && r.Metadata.GetValueOrDefault("skipReason") == "parameters")
                        .Select(r => r.Name).Distinct().OrderBy(n => n).ToList();
                    if (noValue.Count > 0)
                        Log($"Parameter routes with no value ({noValue.Count}): {string.Join(", ", noValue)}");
                    var msg = sessions.Count == 0
                        ? "No role was logged in, so authorization was not checked. Only guest access was tested."
                        : $"Only one role ({sessions[0].Role}) was tested, so authorization was not checked. " +
                        "Role separation (who may open what) needs at least two roles. Add another account in TEST ACCOUNTS.";

                    Log($"WARNING  {msg}");

                    var warn = new TestResult
                    {
                        Category = "Authorization",
                        Name = "Role access matrix",
                        Status = TestStatus.Warning,
                        Severity = Severity.Low,
                        Expected = "At least two logged-in roles to compare route access",
                        Actual = sessions.Count == 0 ? "0 roles" : "1 role",
                        Message = msg
                    };
                    warn.Metadata["classification"] = "single-role";
                    all.Add(warn);
                }

            LastHttpResults = all;
            await SaveAsync("http", all);
            LogHttpSummary(all);
        });
    }

    private void LogRoleMatrix(RoleMatrix matrix, IReadOnlyList<TestResult> issues)
    {
        if (matrix.IsEmpty) return;

        Log($"Role access matrix: {matrix.Rows.Count} route(s) across {matrix.Roles.Count} role(s)");
        foreach (var role in matrix.Roles)
        {
            int Count(string cell) => matrix.Rows.Count(r => r.Cells.GetValueOrDefault(role) == cell);
            Log($"  {role}: {Count(RoleMatrix.Allowed)} allowed, {Count(RoleMatrix.Forbidden)} forbidden, " +
                $"{Count(RoleMatrix.Error)} error, {Count(RoleMatrix.Other)} other");
        }

        if (issues.Count == 0)
        {
            Log("  No access hints found.");
            return;
        }

        Log($"  Access hints ({issues.Count}):");
        foreach (var i in issues.Take(25)) Log($"    WARNING  {i.Message}");
        if (issues.Count > 25) Log($"    ...and {issues.Count - 25} more (see Results)");
    }

    private void LogHttpSummary(IReadOnlyList<TestResult> results)
    {
        static bool IsAborted(TestResult r) => r.Metadata.GetValueOrDefault("classification") == "aborted";

        // Role access hints are listed separately above, not counted as route checks.
        var tested = results
            .Where(r => r.Status != TestStatus.Skipped && !IsAborted(r) && r.Category != "Authorization")
            .ToList();
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

        var loggedIn = tested.Where(r => r.Metadata.GetValueOrDefault("authenticated") == "true").ToList();
        if (loggedIn.Count > 0)
        {
            var perRole = loggedIn
                .GroupBy(r => r.Metadata.GetValueOrDefault("role", "?"))
                .Select(g => $"{g.Key}: {g.Count()}");
            Log($"  Logged-in checks: {loggedIn.Count} ({string.Join(", ", perRole)}); the rest were guest checks");
        }

        var withValues = tested.Count(r => r.Metadata.ContainsKey("routePattern"));
        if (withValues > 0) Log($"  Parameter routes checked with real values: {withValues}");

        if (notRun.Count > 0)
        {
            Log($"  Stopped early, {notRun.Count} route(s) not run. {notRun[0].Message}");
            foreach (var g in notRun.GroupBy(r => r.Metadata.GetValueOrDefault("role", "")))
            {
                var who = string.IsNullOrWhiteSpace(g.Key) ? "" : $"[{g.Key}] ";
                Log($"    Not run {who}({g.Count()}): {string.Join(", ", g.Select(r => r.Name))}");
            }
        }

        static string Label(string code) => code switch
        {
            "auth" => "need auth (no login used)",
            "parameters" => "have parameters (no real value found)",
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
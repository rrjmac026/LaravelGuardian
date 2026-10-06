# File Purposes

This index describes each tracked solution, source, project, and test file. Generated files and existing documentation are omitted.

| File | Purpose |
|---|---|
| `LaravelGuardian.slnx` | Groups the Core, Services, UI, and Tests projects into the solution. |
| `src/LaravelGuardian.Core/Class1.cs` | Default empty starter class; it currently has no application responsibility. |
| `src/LaravelGuardian.Core/LaravelGuardian.Core.csproj` | Defines the Core library target and compiler settings; Core contains shared contracts and models without project dependencies. |
| `src/LaravelGuardian.Core/Interfaces/IAuth.cs` | Contracts for seeded-account discovery, secure credential storage, and the one-time login flow. |
| `src/LaravelGuardian.Core/Interfaces/IBrowser.cs` | **New.** Contract for crawling the running app in a real browser (GET navigation only), optionally with an existing logged-in session. |
| `src/LaravelGuardian.Core/Interfaces/IControllers.cs` | Contract for checking Laravel route actions against controller methods. |
| `src/LaravelGuardian.Core/Interfaces/IEnvironment.cs` | Contracts for owning child processes and starting or stopping the Laravel/Vite environment. |
| `src/LaravelGuardian.Core/Interfaces/IHttp.cs` | Contract for running HTTP checks against discovered routes, optionally with an authenticated session. |
| `src/LaravelGuardian.Core/Interfaces/ILaravel.cs` | Contracts for running Artisan commands and native tests, and for discovering Laravel routes. |
| `src/LaravelGuardian.Core/Interfaces/IMigrations.cs` | Contract for checking migration status without applying migrations. |
| `src/LaravelGuardian.Core/Interfaces/IProjectScanner.cs` | Contracts for scanning Laravel project metadata and detecting installed development tools. |
| `src/LaravelGuardian.Core/Interfaces/IReporting.cs` | Contracts for storing run data, coordinating a run and its evidence, and exporting reports. |
| `src/LaravelGuardian.Core/Models/AuthModels.cs` | Data models for detected accounts, remembered account profiles, and authenticated HTTP sessions. |
| `src/LaravelGuardian.Core/Models/BrowserModels.cs` | **New.** Options for the browser crawl: headless mode, page and depth limits, samples per similar-URL pattern, route seeding, navigation timeout, and exclusion patterns. |
| `src/LaravelGuardian.Core/Models/HttpModels.cs` | Options that configure HTTP-check timeouts, limits, delays, and exclusions. |
| `src/LaravelGuardian.Core/Models/LaravelModels.cs` | Shared data for command outcomes, discovered routes, and route-scan results. |
| `src/LaravelGuardian.Core/Models/ProjectInfo.cs` | Project and tool metadata produced during Laravel project scanning. |
| `src/LaravelGuardian.Core/Models/RunModels.cs` | Run summaries, report paths, and comparison data for identifying new, persistent, and fixed failures. |
| `src/LaravelGuardian.Core/Models/TestResult.cs` | The common result record used by checks, tests, and reporting. |
| `src/LaravelGuardian.Core/Models/TestStatus.cs` | Shared status and severity enums used to classify results. |
| `src/LaravelGuardian.Services/AppPaths.cs` | Defines Guardian's local application-data paths and shared JSON serialization options. |
| `src/LaravelGuardian.Services/ArtisanRunner.cs` | Runs one-shot `php artisan` commands through the process manager. |
| `src/LaravelGuardian.Services/AuthLogin.cs` | Performs a single localhost-only login and returns the session cookies for authenticated checks. |
| `src/LaravelGuardian.Services/BrowserCheckRunner.cs` | **New.** Crawls the app in Playwright Chromium: loads the login session, blocks writes fired by pages (Safe Mode), records console, network and JavaScript errors, takes screenshots of failures, limits similar pages, and classifies each page. Installs Chromium on first use and reports `Blocked` if the browser cannot start. |
| `src/LaravelGuardian.Services/ControllerChecker.cs` | Resolves Laravel route actions to controller files and reports missing or commented-out methods. |
| `src/LaravelGuardian.Services/EnvironmentManager.cs` | Starts and stops the Laravel server and optional Vite process, including readiness and preflight checks. |
| `src/LaravelGuardian.Services/HttpCheckRunner.Response.cs` | Reads and classifies HTTP responses, validates API JSON, detects exceptions, and prepares redacted evidence. |
| `src/LaravelGuardian.Services/HttpCheckRunner.cs` | Selects safe routes, performs HTTP requests, applies skip rules, and classifies check outcomes. **Changed:** the risky-word lists and the skip-rule method are now `internal` so the browser crawler reuses the same Safe Mode rules. |
| `src/LaravelGuardian.Services/LaravelGuardian.Services.csproj` | Defines the Services library, its Core reference, and SQLite/DPAPI dependencies. **Changed:** adds the `Microsoft.Playwright` package. |
| `src/LaravelGuardian.Services/MigrationChecker.cs` | Runs Laravel's read-only migration-status command and converts its output into results. |
| `src/LaravelGuardian.Services/NativeTestRunner.cs` | Runs the Laravel project's Pest/PHPUnit tests and parses JUnit XML into Guardian results. |
| `src/LaravelGuardian.Services/ProcessManager.cs` | Starts, observes, times out, and stops child processes launched by Guardian. |
| `src/LaravelGuardian.Services/ProjectScanner.cs` | Checks Laravel project markers and reads framework and frontend metadata. |
| `src/LaravelGuardian.Services/ReadinessChecker.cs` | Waits for a service port to become available while checking timeout and process state. |
| `src/LaravelGuardian.Services/ReportExporter.cs` | Exports a stored run as HTML and JSON reports, masking email addresses in those exports. |
| `src/LaravelGuardian.Services/RouteScanner.cs` | Runs `artisan route:list` and parses its JSON output into route models. |
| `src/LaravelGuardian.Services/RunSession.cs` | Coordinates the active run, persists source results, and writes/removes evidence files. |
| `src/LaravelGuardian.Services/RunStore.cs` | Stores run summaries and results in SQLite and retrieves run history. |
| `src/LaravelGuardian.Services/SecretStore.cs` | Saves, loads, and removes remembered project credentials encrypted with Windows DPAPI. |
| `src/LaravelGuardian.Services/SeederScanner.cs` | Reads Laravel seeders and factories to find candidate test accounts. |
| `src/LaravelGuardian.Services/ToolDetector.cs` | Detects PHP, Composer, Node.js, and NPM and reads their version output. |
| `src/LaravelGuardian.UI/App.xaml` | Declares WPF application-level resources and application startup metadata. |
| `src/LaravelGuardian.UI/App.xaml.cs` | Configures logging and dependency injection, creates the main window, and handles shutdown and unhandled errors. **Changed:** registers the browser check runner. |
| `src/LaravelGuardian.UI/AssemblyInfo.cs` | Supplies WPF theme-resource lookup metadata for the UI assembly. |
| `src/LaravelGuardian.UI/LaravelGuardian.UI.csproj` | Defines the Windows WPF executable, UI dependencies, and Core/Services project references. |
| `src/LaravelGuardian.UI/MainWindow.xaml` | Defines the main dashboard layout and its project, environment, check, account, and activity-log controls. **Changed:** redesigned as a dark card layout with custom button, input, dropdown and checkbox styles, tool chips, and a new **Run Browser Checks** button. |
| `src/LaravelGuardian.UI/MainWindow.xaml.cs` | Connects dashboard-specific WPF behavior, including browsing, password-box synchronization, and activity-log display. |
| `src/LaravelGuardian.UI/ResultsWindow.xaml` | Defines the run-history and result-inspection window layout. **Changed:** restyled to match the dashboard, with colored status text and tinted rows for failures, warnings and blocked results. |
| `src/LaravelGuardian.UI/ResultsWindow.xaml.cs` | Connects Results-window lifecycle behavior and its view model to the WPF window. |
| `src/LaravelGuardian.UI/ViewModels/MainViewModel.Browser.cs` | **New.** Holds the dashboard command for browser checks: logs in once, runs the crawl, prints each page once with deduplicated console/network issues, saves results, and logs a summary with repeated-error counts. |
| `src/LaravelGuardian.UI/ViewModels/MainViewModel.Controllers.cs` | Holds the dashboard command and UI behavior for the route-to-controller check. |
| `src/LaravelGuardian.UI/ViewModels/MainViewModel.Migrations.cs` | Holds the dashboard command and UI behavior for checking migration status. **Changed:** its busy/project change hooks also refresh the Run Browser Checks button. |
| `src/LaravelGuardian.UI/ViewModels/MainViewModel.cs` | Owns dashboard state and commands for scanning, environment control, tests, routes, HTTP checks, accounts, logging, and result persistence. |
| `src/LaravelGuardian.UI/ViewModels/ResultsViewModel.cs` | Supplies run-history loading, filtering, comparison, result details, and report/evidence commands to the Results window. |
| `tests/LaravelGuardian.Tests/LaravelGuardian.Tests.csproj` | Defines the xUnit test project and its test SDK and coverage dependencies. |
| `tests/LaravelGuardian.Tests/UnitTest1.cs` | Default empty xUnit starter test; it currently verifies no application behavior. |

## Recent changes (2026-10-07)

**Added: browser engine (roadmap Phase 5, first slice)**
- Playwright Chromium crawl of safe GET routes plus same-origin links, with login cookies reused from the HTTP login.
- Detects server errors, uncaught JavaScript exceptions, console errors, and failed or 4xx/5xx requests; screenshots for failures.
- Safe Mode: skips destructive-looking URLs, blocks non-GET requests fired by pages, and reports what it blocked or skipped.
- Crawl limits: 80 pages, depth 3, 2 samples per URL pattern such as `/admin/audit-logs/{id}`; non-page endpoints (`/up`, `sanctum/*`, `.well-known/*`) are excluded.
- Chromium is downloaded automatically on first run; if it cannot start the result is `Blocked`, not a pass.

**Changed**
- Dashboard and Results windows restyled (dark card design).
- HTTP runner's Safe Mode rules shared with the browser crawler.

**Verified on `scms`:** login and cookie transfer, the known 500 pages reported as failures with screenshots, repeated-issue dedupe, similar-page sampling.

**Known gaps**
- Browser login is not implemented: SPA/Inertia projects such as `dlis-web` fall back to guest pages only.
- Query strings are dropped when de-duplicating URLs.
- Cancel takes effect between pages.
- Screenshot paths are stored in the result and evidence JSON; confirm they appear in the Results window.
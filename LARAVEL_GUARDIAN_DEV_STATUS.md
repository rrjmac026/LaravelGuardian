# Laravel Guardian: Development Status

*Snapshot date: 2026-10-07*
*Solution location: `F:\Projects\LaravelGuardian`*
*Test target used so far: `F:\Projects\scms` (Laravel 12, Vite, Pest)*

This document records what has been built so far, what every file does, how the pieces connect, what has been verified on a real project, and what we plan to add. It is measured against the master draft (`Laravel_Guardian_Full_Project_Draft.pdf`).

---

## 1. Where we are right now

| Item | Status |
|---|---|
| Current position | **The core pipeline is built and verified on `scms`; a first Playwright browser-check slice is implemented and has also been exercised there.** |
| Verified on a real project | Project scan, environment boot, native tests, route discovery, migration status, guest and logged-in HTTP checks, controller audit flow, and browser checks have been exercised on `scms`. |
| Still to verify | Confirm exported email masking and screenshot access in Results; validate controller audit on a live project. Browser login for SPA/Inertia apps is not implemented yet. |
| MVP (draft §22) | Complete. The original MVP flow is implemented; browser checks are an additional Phase 5 slice. |
| Next decision | Close the report/screenshot verification gaps, then choose between extending browser coverage, Phase 8 static checks, or multi-role logged-in checks. |

### Steps completed in this build

| Step | Name | Roadmap phase | Status |
|---|---|---|---|
| 1 | Foundation: solution, MVVM, DI, project scanner, tool detection, dashboard | Phase 1 | ✅ Done and verified |
| 2 | Environment Engine: process manager, Laravel and Vite start/stop, readiness checks, clean shutdown | Phase 2 | ✅ Done and verified |
| 2b | Terminal-style copyable Activity log (selectable text, Copy all, Clear, smart auto-scroll) | UI polish | ✅ Done and verified |
| 3 | Laravel Engine: artisan runner, native test runner (JUnit parsing), route discovery | Phase 3 | ✅ Done and verified |
| 4 | HTTP/API Engine: safe GET health checks with classification, evidence, early stop on repeated errors | Phase 4 | ✅ Done and verified (guest and logged-in runs on `scms`) |
| 4b | Seeded test accounts: scan seeders/factories, remember the account, log in once, check protected routes | Phase 6 slice | ✅ Done and verified (admin account, 50 logged-in checks) |
| 5 | Results, run history, evidence and report export | Phase 7 | ✅ Delivered; Results window verified, export masking still to verify |
| 6 | Migration status check (`php artisan migrate:status`, read-only) | Phase 3 | ✅ Done and verified (14 of 14 migrations ran on `scms`) |
| 7 | Route-to-controller check | Phase 8 slice | ✅ Implemented in the app; needs a live-project validation pass |
| 8 | Browser checks (Playwright/Chromium) | Phase 5 slice | ✅ First slice implemented and exercised on `scms`; broader coverage remains open |

---

## 2. Progress report (2026-10-02 → 2026-10-07)

### What was built or changed

| Area | Change |
|---|---|
| HTTP engine | Optional logged-in pass: routes that need login are checked with the session from one login. New classifications `forbidden-for-role`, `session-lost`, `aborted`. A logged-in 403 counts as a pass (role separation working); a logged-in 401 or a redirect to login is a warning. |
| HTTP engine | Early stop: after 3 identical server errors in a row the remaining routes are marked `Blocked` (`aborted`) with a "probably share one cause" message, instead of waiting 10 s per request. |
| HTTP engine | Better error evidence: quote-aware HTML-to-text, exception class plus message (e.g. `SQLSTATE[HY000] [1049] Unknown database`), shown in the log line. |
| HTTP engine | Extra words skipped for logged-in GET routes (`approve`, `cancel`, `clear`, `reset`, `send`, `mark`, ...) because those paths often change data. |
| Static analysis | New route-to-controller audit: `IControllers.cs`, `ControllerChecker.cs`, the dashboard button, and a summary that shows missing or commented-out controller methods. |
| Code layout | `HttpCheckRunner` split into two partial files: run logic and response analysis. |
| Auth | `SeederScanner`, `SecretStore` (Windows DPAPI, one account per project folder) and `AuthLogin` (one POST to `/login`, localhost only, never logs the password). Account row in the dashboard (dropdown of detected accounts, email, password, Remember, Forget). |
| Native tests | `NativeTestRunner.Parse` strips the repeated test name from failure messages. |
| Reporting | `ReportExporter` masks email addresses in `report.html` and `report.json`, and the report note now says how many routes were checked logged in. |
| Migrations | New read-only `MigrationChecker` and a **Check Migrations** button. |
| Dashboard | New buttons (Run HTTP Checks, Check Migrations), button rows wrap, window height 700. `MainViewModel` is now `partial` (`MainViewModel.Migrations.cs`). |

### 2026-10-07: Browser engine and UI refresh

| Area | Change |
|---|---|
| Browser engine | Added `IBrowserCheckRunner`, `BrowserOptions`, and `BrowserCheckRunner` using Playwright Chromium. The dashboard can run a browser crawl, attempt the existing localhost login, reuse successful session cookies, stream per-page results, and persist them under the `browser` source. |
| Browser Safe Mode | Seeds the crawl from safe GET routes and same-origin links. It shares the HTTP runner's risky-path rules and blocks non-GET requests triggered by page scripts. |
| Browser findings | Checks page status, uncaught JavaScript exceptions, console errors, failed requests, and 4xx/5xx subrequests. Failure pages can get screenshots; repeated console/network issues are deduplicated in the dashboard summary. |
| Crawl limits | Defaults to 80 pages, depth 3, a 15-second navigation timeout, and 2 samples per similar URL pattern. Chromium is installed on first use when missing; engine startup/install failures are reported as `Blocked`. |
| UI | Dashboard and Results windows were restyled with a dark card layout; the dashboard adds **Run Browser Checks**, and Results rows/status labels are tinted by outcome. |

### What was verified on `scms`

| Run | Result |
|---|---|
| Guest HTTP checks (database created and seeded) | 9 checked: 8 passed, 1 failed (`/register`). |
| Logged-in HTTP checks, first run (admin account) | Login passed (landed on `/admin/dashboard`). 59 checked: 31 passed, 8 failed, 20 warnings (19 were 403s on other roles' pages). |
| Logged-in HTTP checks after the report changes | 59 checked: **51 passed, 8 failed, 0 warnings**. Breakdown: 27 ok, 19 forbidden-for-role, 8 server-error, 2 auth-redirect, 2 redirect-external, 1 redirect. |
| Migrations | All 14 migrations have run. |
| Seeder scan | 6 accounts found (1 admin, 2 counselors, 2 students, 1 without a role). |
| Browser checks | Verified login-cookie transfer, known 500 pages reported as failures with screenshots, repeated-issue deduplication, and sampling of similar pages. |

### Bugs found in `scms` (all real, none are Guardian problems)

| Finding | Detail |
|---|---|
| Missing controller methods | `/admin/counseling-sessions/create` → `AdminSessionController::create()`; `/admin/users/create` → `UserController::create()` (the method is commented out); `/admin/users/template` → `UserController::downloadTemplate()`. |
| Unused Fortify routes | `/register` fails on `RegisterViewResponse`. `scms` has no self-registration (admins create or import users), so the fix is to remove `Features::registration()` from `config/fortify.php` and delete `RegistrationTest`. `/user/confirm-password` fails on `ConfirmPasswordViewResponse`; the three `/user/two-factor-*` routes fail only because they redirect to it. |
| Open debug route | `/debug/google-config` returns 200 to guests and to the admin. |
| Data loss in `UserController::update()` | The student validation rules only covered a few fields, so `lrn`, `strand`, birthdate and the parent details were saved as empty on every edit. A corrected controller was provided; **needs a manual check** (edit a student with details, save, reopen). |
| Native tests | 5 of 25 still fail: 2 are `RegistrationTest` (feature does not exist), 1 expects `/dashboard` but the app redirects to `/student/dashboard`, 2 send `name` while the app requires first and last name. |
| Not a finding | The 5.6 s load of `/admin/students/create` was a one-time first load (732 ms next run). The ~500 ms on every page is `artisan serve`. |

---

## 3. What we will add

Order is a suggestion. Items marked 🔵 are proposed and need a go before any code is written.

| # | Addition | Phase | Status | What it does |
|---|---|---|---|---|
| 1 | **Route-to-controller check** | 8 slice | ✅ Implemented in the app | Reads every route's action, opens the controller file (via the `App\` mapping in `composer.json`), strips comments, and reports methods that do not exist. Finds bugs like `UserController::create()` without opening a page and also covers POST routes and routes with parameters. Inherited or trait methods are reported as warnings, not failures. Closures, invokable and vendor controllers are skipped. New files: `IControllers.cs`, `ControllerChecker.cs`, `MainViewModel.Controllers.cs`, plus the dashboard button and DI registration. |
| 2 | **Model vs migration column check** | 8 slice | ⬜ Planned | Compares columns created by migrations with the fields models and controllers use (`$fillable`, `Student::create([...])`). Finds fields that are saved but have no column, and columns nothing fills. Hints, not proof. |
| 3 | **Validation vs save mismatch hints** | 8 slice | ⬜ Planned | Flags controllers that save fields their validation rules never allow (the `update()` bug class). |
| 4 | **Multi-role logged-in checks** | 6 | ⬜ Planned | Run the logged-in pass once per role (admin, counselor, student) using the accounts the seeder scan already finds, and show an allowed/forbidden matrix per route. |
| 5 | **Risky public route warning** | 4 | ⬜ Idea | Warn when a guest can open paths like `debug`, `telescope`, `phpinfo`, `horizon`. |
| 6 | **Safe form validation** | 6 | ⬜ Later | Discover forms and send scenario-authorized POSTs (empty required fields) only when a scenario allows it. |
| 7 | **Scenario files (expectations)** | 9 | ⬜ Later | "Given these inputs, expect this output" for important calculations and workflows. |
| 8 | **Baseline snapshots** | 9 | ⬜ Idea | Record what a page or API returns for known inputs and flag later changes (characterization testing). |
| 9 | **Coverage view** | 8 / 10 | ⬜ Idea | Use pcov or Xdebug to list controllers and methods that neither the project's tests nor Guardian exercised. |
| 10 | **Playwright browser engine** | 5 | 🟡 First slice implemented | Bounded safe browser crawl and error/screenshot checks are in the app and exercised on `scms`. Follow-ups: SPA/Inertia login, query-aware coverage, in-page cancellation, screenshot access in Results, and interactive/form checks. |
| 11 | **Reliability pass** | 2 / tests | ⬜ Planned | Windows Job Object for child processes; first real tests in `LaravelGuardian.Tests`; run retention and cleanup; split more of `MainViewModel`. |
| 12 | **AI-assisted diagnosis** | 10 | ⬜ Future | Suggest likely causes from collected evidence. Never edits code. |

---

## 4. Technology and tooling

- **Language / runtime:** C# on .NET 10, WPF (`net10.0-windows` for the UI project)
- **Pattern:** MVVM with `CommunityToolkit.Mvvm` (source-generated `[ObservableProperty]` and `[RelayCommand]`)
- **DI and hosting:** `Microsoft.Extensions.Hosting`
- **Logging:** Serilog, rolling file at `%LocalAppData%\LaravelGuardian\logs\guardian-YYYYMMDD.log`
- **Run data:** SQLite database at `%LocalAppData%\LaravelGuardian\guardian.db`; run evidence and exports under `%LocalAppData%\LaravelGuardian\runs\<run-id>\`
- **Saved test accounts:** `%LocalAppData%\LaravelGuardian\accounts.json` (passwords encrypted with Windows DPAPI for the current Windows user)
- **Dev workflow:** VS Code + C# Dev Kit, run with `dotnet run --project src/LaravelGuardian.UI`
- **Rule:** close the running Guardian window before rebuilding, or the DLL copy fails with a file-lock error. After pulling in new files, run `dotnet clean` first.

NuGet packages: `LaravelGuardian.UI` uses `CommunityToolkit.Mvvm`, `Microsoft.Extensions.Hosting`, `Serilog.Extensions.Hosting`, and `Serilog.Sinks.File`; `LaravelGuardian.Services` uses `Microsoft.Data.Sqlite`, `Microsoft.Playwright`, and `System.Security.Cryptography.ProtectedData`.

Project references: `UI → Core, Services`; `Services → Core`; `Core → nothing`.

---

## 5. Folder structure

```
LaravelGuardian/
├── README.md
├── LARAVEL_GUARDIAN_DEV_STATUS.md
├── LaravelGuardian.slnx
├── src/
│   ├── LaravelGuardian.UI/
│   │   ├── App.xaml
│   │   ├── App.xaml.cs
│   │   ├── MainWindow.xaml
│   │   ├── MainWindow.xaml.cs
│   │   ├── ResultsWindow.xaml
│   │   ├── ResultsWindow.xaml.cs
│   │   └── ViewModels/
│   │       ├── MainViewModel.cs
│   │   │   ├── MainViewModel.Browser.cs
│   │       ├── MainViewModel.Controllers.cs (Check Controllers command)
│   │       ├── MainViewModel.Migrations.cs   (Check Migrations command)
│   │       └── ResultsViewModel.cs
│   │
│   ├── LaravelGuardian.Core/
│   │   ├── Models/
│   │   │   ├── AuthModels.cs         (SeededAccount, AuthProfile, AuthSession)
│   │   │   ├── BrowserModels.cs       (BrowserOptions)
│   │   │   ├── TestStatus.cs         (TestStatus + Severity enums)
│   │   │   ├── TestResult.cs
│   │   │   ├── ProjectInfo.cs        (ProjectInfo + ToolInfo)
│   │   │   ├── LaravelModels.cs      (CommandResult, RouteInfo, RouteScanResult)
│   │   │   ├── HttpModels.cs         (HttpCheckOptions)
│   │   │   └── RunModels.cs          (RunSummary, ReportPaths, RunComparison, RunComparer)
│   │   └── Interfaces/
│   │       ├── IAuth.cs              (ISeederScanner + ISecretStore + IAuthLogin)
│   │       ├── IBrowser.cs           (IBrowserCheckRunner)
│   │       ├── IProjectScanner.cs    (IProjectScanner + IToolDetector)
│   │       ├── IEnvironment.cs       (IProcessManager + IEnvironmentManager)
│   │       ├── ILaravel.cs           (IArtisanRunner + INativeTestRunner + IRouteScanner)
│   │       ├── IMigrations.cs        (IMigrationChecker)
│   │       ├── IHttp.cs              (IHttpCheckRunner)
│   │       └── IReporting.cs         (IRunStore, IRunSession, IReportExporter)
│   │
│   └── LaravelGuardian.Services/
│       ├── BrowserCheckRunner.cs    (Playwright browser crawl and page checks)
│       ├── AuthLogin.cs             (single login step for protected-route checks)
│       ├── SeederScanner.cs         (reads seeders/factories for candidate accounts)
│       ├── SecretStore.cs           (encrypts remembered account credentials per project)
│       ├── ProjectScanner.cs
│       ├── ToolDetector.cs
│       ├── ProcessManager.cs
│       ├── ReadinessChecker.cs
│       ├── EnvironmentManager.cs
│       ├── ArtisanRunner.cs
│       ├── NativeTestRunner.cs
│       ├── RouteScanner.cs
│       ├── ControllerChecker.cs     (checks whether route actions map to real controller methods)
│       ├── MigrationChecker.cs      (read-only migrate:status)
│       ├── HttpCheckRunner.cs       (run logic, skip rules, classification)
│       ├── HttpCheckRunner.Response.cs (body reading, exception detection, evidence snippets)
│       ├── AppPaths.cs              (local data paths and JSON options)
│       ├── RunStore.cs              (SQLite persistence)
│       ├── RunSession.cs            (current-run coordination and evidence files)
│       └── ReportExporter.cs        (HTML and JSON report generation, email masking)
│
└── tests/
    └── LaravelGuardian.Tests/       (default xunit project, still empty)
```

---

## 6. File-by-file reference

### 6.1 `LaravelGuardian.Core`: shared models and contracts

Core has no dependencies. It holds the data shapes and interfaces every other project agrees on.

#### Models

| File | Contents | Purpose |
|---|---|---|
| `TestStatus.cs` | `enum TestStatus { Pass, Fail, Warning, Skipped, Blocked }`<br>`enum Severity { Info, Low, Medium, High, Critical }` | The honest status vocabulary from the draft. `Blocked` means "could not run", never a fake pass. |
| `TestResult.cs` | `TestResult` class | The **unified result model** every module returns. Fields: `Id`, `Category`, `Name`, `Status`, `Severity`, `StartedAt`, `Duration`, `Url`, `HttpStatus`, `Expected`, `Actual`, `Message`, `ExceptionType`, `ExceptionMessage`, `EvidencePath`, `ScreenshotPath`, `ConsoleErrors`, `NetworkErrors`, `Metadata` (string dictionary). |
| `ProjectInfo.cs` | `ProjectInfo`, `ToolInfo` record | `ProjectInfo`: path, name, `IsLaravel`, missing items, Laravel version constraint, `HasPest`, `HasPhpUnit`, `UsesVite`, `FrontendFramework`. `ToolInfo`: name, found, version string. |
| `LaravelModels.cs` | `CommandResult`, `RouteInfo`, `RouteScanResult` | `CommandResult`: outcome of a one-shot command (exit code, stdout, stderr, duration, timed-out, started). `RouteInfo`: one route with computed helpers `IsGet`, `HasParameters`, `IsApi`, `RequiresAuth`. `RouteScanResult`: a `TestResult` plus the route list. |
| `HttpModels.cs` | `HttpCheckOptions` | Knobs for the HTTP engine: timeout (30 s), slow threshold (5000 ms), delay between requests, max routes (200), exclude patterns. |
| `AuthModels.cs` | `SeededAccount`, `AuthProfile`, `AuthSession` | `SeededAccount`: email, role, password if found, source file. `AuthProfile`: the remembered account for one project. `AuthSession`: login outcome, the cookies reused by logged-in checks, and the login `TestResult`. |
| `BrowserModels.cs` | `BrowserOptions` | Browser crawl configuration: headless mode, page/depth and similar-URL limits, route seeding, navigation timeout, and excluded paths. |
| `RunModels.cs` | `RunSummary`, `ReportPaths`, `RunComparison`, `RunComparer` | Run header/counter data, exported file paths, and comparison of new, persistent, and fixed failures. Results are matched by source metadata plus result name; only failures becoming passes count as fixed. |

#### Interfaces

| File | Interfaces | Purpose |
|---|---|---|
| `IAuth.cs` | `ISeederScanner`, `ISecretStore`, `IAuthLogin` | Find candidate seeded accounts, remember project credentials securely, and log in once for authenticated route checks. |
| `IBrowser.cs` | `IBrowserCheckRunner` | Contract for crawling an app in a real browser and returning per-page results, optionally reusing login cookies. |
| `IProjectScanner.cs` | `IProjectScanner`, `IToolDetector` | Scan a folder into `ProjectInfo`; detect PHP/Composer/Node/NPM. |
| `IEnvironment.cs` | `IProcessManager`, `IEnvironmentManager` | Start, track, run and stop processes Guardian owns; start/stop the whole Laravel + Vite environment. |
| `ILaravel.cs` | `IArtisanRunner`, `INativeTestRunner`, `IRouteScanner` | Run artisan commands; run Pest/PHPUnit; discover routes. |
| `IMigrations.cs` | `IMigrationChecker` | Ask Laravel which migrations have run (read-only). |
| `IHttp.cs` | `IHttpCheckRunner` | Run safe HTTP checks against discovered routes, with an optional logged-in session and a live per-result callback. |
| `IControllers.cs` | `IControllerChecker` | Check whether each route action resolves to a real controller method and report missing/commented-out methods. |
| `IReporting.cs` | `IRunStore`, `IRunSession`, `IReportExporter` | Persist run summaries/results, coordinate the active run and per-result evidence files, and export HTML/JSON reports. |

---

### 6.2 `LaravelGuardian.Services`: the engines

#### `ProjectScanner.cs`
**Function:** Validates and describes a Laravel project folder.
- Checks that `artisan`, `composer.json`, `app/`, `routes/`, `public/` and `storage/` exist, and lists what's missing.
- Reads `composer.json` for the `laravel/framework` constraint, `pestphp/pest` and `phpunit/phpunit`.
- Reads `package.json` for `vite`, `react` and `vue`.
- Returns a `ProjectInfo`. Note: the Laravel version is the *constraint* (e.g. `^12.0`), not the installed version.

#### `ToolDetector.cs`
**Function:** Detects whether PHP, Composer, Node and NPM are installed and gets their version strings.
- Runs the command through `cmd.exe /c` (needed for `.bat`/`.cmd` shims on Windows), with a 10-second timeout.
- Returns `ToolInfo` (found/not found plus the first line of output).

#### `ProcessManager.cs`
**Function:** Central owner of every child process Guardian starts. Guarantees Guardian only ever kills processes it started itself.
- `Start(...)`: launches a long-lived process (Laravel server, Vite), captures stdout/stderr line by line, strips ANSI colour codes, decodes as UTF-8, raises `OutputReceived` and `ProcessExited` events, and tracks the process by name.
- `RunAsync(...)`: runs a one-shot command to completion with a timeout. Tracked too, so closing the app mid-run kills it. Supports `streamOutput` on/off (off for huge output such as the route JSON). Closes stdin immediately so nothing can hang waiting for input.
- `IsRunning(name)`: whether a tracked process is alive.
- `StopAllAsync()`: kills each tracked process **and its whole process tree** (needed because `npm run dev` and `artisan serve` spawn children), waiting up to 5 s each.

#### `ReadinessChecker.cs`
**Function:** Waits for a service to be ready without fixed sleeps.
- `WaitForPortAsync(host, port, timeout, processAlive, ct)`: plain TCP connect polling every 300 ms. Proves the port is listening **without executing any app code**. Gives up early if the process dies. (An earlier version used a full HTTP request and wrongly timed out on slow cold starts, so it was replaced.)

#### `EnvironmentManager.cs`
**Function:** Starts and stops the Laravel + Vite environment and reports structured results.
- Picks a **free port** (never assumes 8000), so it can't collide with a server the developer already runs.
- Pre-flight: `vendor/autoload.php` must exist (else `Blocked`: run `composer install`); `node_modules/` must exist for Vite (else `Blocked`: run `npm install`).
- Starts `php artisan serve --host=127.0.0.1 --port=N --no-reload`, waits up to 90 s for the port.
- Starts `cmd /c npm run dev` if Vite was detected; detects readiness by watching Vite's stdout for `ready in` / `Local:`. Distinguishes Pass / Warning (running but no ready message) / Blocked (exited immediately).
- On Laravel failure, stops anything it started and reports `Blocked`.
- Exposes `BaseUrl`, `IsRunning`, `StartAsync(...)`, `StopAsync()`.

#### `ArtisanRunner.cs`
**Function:** Thin wrapper to run `php artisan <args>` in a project folder through the `ProcessManager`. Labels output as `[artisan:<command>]` in the log.

#### `NativeTestRunner.cs`
**Function:** Runs the project's own Pest/PHPUnit suite and converts it to unified results.
- Skips if no Pest/PHPUnit; blocks if `vendor/` is missing.
- **Database safety check:** blocks unless `phpunit.xml(.dist)` sets `DB_CONNECTION` or `DB_DATABASE` (or `.env.testing` exists), because `RefreshDatabase` could wipe a development database. The user can override with the "Allow tests on the project's database (unsafe)" checkbox. This is a heuristic: it confirms tests *override* the DB, not that the target is disposable.
- Runs `php artisan test --no-ansi --log-junit=<temp>\junit.xml` (10-minute timeout), then parses the **JUnit XML** (more reliable than scraping console text).
- One `TestResult` per test case: Pass / Fail (with exception type, cleaned message, up to 4000 chars of detail) / Warning / Skipped. The short message strips the repeated test name that Pest puts at the start of the failure text. Adds a warning if the process exited non-zero without a reported failure, or if no tests were found.
- Cleans up the temp report folder afterwards.

#### `RouteScanner.cs`
**Function:** Discovers routes via `php artisan route:list --json --no-ansi`.
- Tolerates PHP warnings printed before the JSON.
- Splits `GET|HEAD` into methods (dropping HEAD), reads uri, name, action, domain and middleware.
- Returns a `RouteScanResult`; failures become `Blocked` / `Fail` with the error text.

#### `ControllerChecker.cs`
**Function:** Audits route actions against the actual project controller files.
- Reads each route action in the form `App\Http\Controllers\XController@method`.
- Resolves the `App\` PSR-4 mapping from `composer.json` and checks the controller file for the method.
- Treats inherited or trait-defined methods as warnings instead of hard failures.
- Reports missing methods, commented-out copies, and skipped route categories such as closures, invokable controllers, or vendor/framework controllers.
- Returns a `TestResult` list grouped under the `Controllers` category for dashboard logging and saved results.

#### `MigrationChecker.cs`
**Function:** Read-only check of which migrations have run, via `php artisan migrate:status --no-ansi` (60 s timeout). Guardian never runs `migrate` itself.
- **Pass:** all migrations have run. **Warning:** some are pending (names kept in `Metadata["pending"]`). **Fail:** the database has no migrations table. **Blocked:** it could not ask (vendor missing, database unreachable, timeout), with the most useful error line. **Skipped:** no `database/migrations` folder.
- Needs the project's database to be reachable, but not the Guardian environment.

#### `HttpCheckRunner.cs` and `HttpCheckRunner.Response.cs`
**Function:** Safe-mode HTTP checks against the running server, optionally with a logged-in session. One `partial` class in two files: `HttpCheckRunner.cs` holds the run logic, skip rules and classification; `HttpCheckRunner.Response.cs` holds body reading, JSON validation, exception detection and redacted evidence snippets.
- **What it tests:** GET routes with no parameters, no domain binding, and no destructive-looking words in the path (`logout`, `delete`, `export`, `download`, etc.). Routes that need login are checked only when a valid `AuthSession` is supplied, and with extra skipped words (`approve`, `cancel`, `clear`, `reset`, `send`, `mark`, ...) because those GET paths often change data. Optional wildcard exclude patterns.
- **What it skips (reported as `SKIPPED` with a reason code):** `method` (non-GET), `parameters`, `auth` (no login used), `domain`, `excluded`, `duplicate`, `limit`.
- **How:** one request at a time, 30 s timeout, redirects followed manually (max 5, same origin only, loop detection). Guest requests use a fresh client with no cookies; logged-in requests use a client that reuses the cookies from the single login.
- **Classification** (`Metadata["classification"]`): `ok`, `redirect`, `auth-redirect`, `redirect-external`, `redirect-loop`, `redirect-no-location`, `unauthorized`, `forbidden`, `forbidden-for-role`, `session-lost`, `not-found`, `server-error`, `invalid-json`, `api-returned-html`, `slow`, `rate-limited`, `unavailable`, `client-error`, `timeout`, `unreachable`, `aborted`.
- **Verdicts:** 2xx = Pass; guest redirected to login and guest 401/403 = Pass (expected protection); logged-in 403 = Pass (`forbidden-for-role`, role separation working); logged-in 401 or redirect to login = Warning (`session-lost`); 404 = Fail; 5xx = Fail (High) with the exception class and message when detectable (JSON `exception`/`message`, or `SQLSTATE`/class name on an HTML debug page); slow responses and API-returned-HTML = Warning; server unreachable = Blocked and the run stops.
- **Early stop:** after 3 identical server errors in a row, the remaining testable routes are marked `Blocked` (`aborted`) with a "probably share one cause" reason instead of being requested.
- **Evidence** (failures and warnings only): response headers with cookies and tokens redacted, content type, and a privacy-limited body snippet (HTML reduced to title + plain text, secrets masked, 2000 chars max). Kept in `Metadata` until the run session saves it.

#### `BrowserCheckRunner.cs`
**Function:** Crawls the running app with Playwright Chromium and records browser-level results.
- Seeds from `/` and, when enabled, safe non-API GET routes from `route:list`; follows same-origin links up to the configured depth and page cap.
- Reuses `HttpCheckRunner` skip rules and risky-path tokens. Query strings and fragments are removed to avoid revisiting variations; file-like paths and configured exclusions are skipped.
- Allows page GET/HEAD/OPTIONS requests and aborts other methods initiated by page scripts. This is a browser crawl, not a form-submission or interaction engine.
- Captures uncaught page exceptions, console errors, failed requests, and 4xx/5xx responses. Classifies page/server/network issues and stores screenshots for failures, console errors, and lost sessions when capture succeeds.
- Limits duplicate pages by URL pattern (numeric/GUID segments become `{id}`). Reports page-limit, similar-page, and blocked-write counts as skipped results.
- Installs Chromium on first use when its executable is missing. If Playwright or Chromium cannot start, returns a `Blocked` engine result.

#### `AuthLogin.cs`, `SeederScanner.cs`, and `SecretStore.cs`
**Function:** The auth workflow.
- `SeederScanner` reads `database/seeders` and `database/factories` (read-only) for literal emails, roles (`'role' => ...`, `assignRole(...)`) and passwords (`Hash::make('...')`, `bcrypt('...')`). A factory's literal password is used when a seeder sets none. Accounts are de-duplicated by email.
- `SecretStore` remembers one account per project folder in `accounts.json`; the password is encrypted with Windows DPAPI (current user only). Supports save, load and delete.
- `AuthLogin` does the one POST to `/login`: loads the page, reads the CSRF token, posts `_token`, `email` and `password`, follows the redirect chain, and fails on a redirect back to login, a two-factor redirect, 419, 422 or 429. It refuses to run unless the base URL is localhost or loopback, and the password never goes to the log or a result. It returns an `AuthSession` with the cookies and a `TestResult`.
- The dashboard exposes a dropdown of detected accounts, email and password boxes, a Remember checkbox and a Forget button. A saved account takes priority over the seeder suggestion.

#### `AppPaths.cs`
**Function:** Defines Guardian's per-user data layout and shared JSON serialization settings.
- Places persistent data under `%LocalAppData%\LaravelGuardian`.
- Exposes the SQLite database path (`guardian.db`), run directory (`runs\<run-id>`), and evidence directory (`runs\<run-id>\evidence`).
- Provides compact and indented JSON options, serializing enums as strings for readable evidence and report files.

#### `RunStore.cs`
**Function:** SQLite-backed storage for run summaries and structured results.
- Creates `test_runs` and `test_results` tables and an index by run/source on first use. The database is `%LocalAppData%\LaravelGuardian\guardian.db`.
- `CreateRunAsync` inserts a run with the project name/path and start/update timestamps.
- `ReplaceSourceAsync` transactionally replaces all result rows for one source (`environment`, `tests`, `routes`, `auth`, `migrations`, `http`, or `browser`), stores `TestResult` as JSON alongside queryable summary columns, and recalculates run totals by status.
- Returns old evidence paths so the session layer can remove files that are no longer referenced after a source rerun.
- Supports loading recent runs (default limit 100), an individual run summary, and all results for a run.

#### `RunSession.cs`
**Function:** Owns the active run lifecycle and writes evidence files before persisting results.
- Creates a run the first time results are recorded, and starts another automatically when the selected project path changes. The Results window's **New run** command resets this in-memory selection; the next recorded source then creates a fresh run.
- Serializes concurrent writes through a `SemaphoreSlim` while creating/updating the run.
- Adds `Metadata["source"]` to each result, writes JSON evidence for `Fail`, `Warning`, and `Blocked` results, and sets `EvidencePath` before the SQLite write.
- After replacing a source, removes files belonging to its previous evidence rows. Evidence redaction is best-effort only; its payload includes result messages, exceptions, and metadata.

#### `ReportExporter.cs`
**Function:** Exports a stored run to standalone HTML and JSON.
- Loads the run and its results through `IRunStore`; writes `report.html` and `report.json` into the run's directory.
- Both files go through `MaskEmails`, so `erven.granada@lccdo.edu.ph` becomes `er***@lccdo.edu.ph`. Only the exported files are masked; the evidence JSON files and the SQLite history keep the real emails locally.
- The HTML report includes status-count summaries, failures/blocked/warnings grouped by source, result detail and metadata, skipped/not-tested reasons, and an expandable list of passing results. A note at the top says whether logged-in checks were used and how many routes they covered.
- HTML text is encoded before insertion. Returns both paths as `ReportPaths`; the UI opens the generated HTML report after export.

---

### 6.3 `LaravelGuardian.UI`: the desktop app

#### `App.xaml` / `App.xaml.cs`
**Function:** Application bootstrap.
- `StartupUri` removed (the window needs a ViewModel injected).
- Configures Serilog, builds the DI host, registers all services as singletons, shows `MainWindow`.
- `OnExit` synchronously calls `StopAllAsync()` so closing the app kills every Guardian-owned process.
- Global exception handlers (UI thread, background thread, unobserved tasks) log and show the real error in a message box, which was added after the first crash investigation.

**Registered services:** project/tool scanning, process/environment management, Laravel runners, `IMigrationChecker`, `IHttpCheckRunner`, `IBrowserCheckRunner`, `ISeederScanner`, `ISecretStore`, `IAuthLogin`, `IRunStore`, `IRunSession`, `IReportExporter`, `MainViewModel`, `ResultsViewModel`, `MainWindow`, and transient `ResultsWindow`. Run services are singletons so both windows share the same session and store.

#### `MainWindow.xaml` / `MainWindow.xaml.cs`
**Function:** The single dashboard window.
- Project path box + **Browse**.
- Summary line (Laravel version, frontend, Vite, test framework) and detected tool versions.
- Buttons (in a wrapping row): **Run Tests**, **Discover Routes**, **Check Migrations**, **Check Controllers**, **Run HTTP Checks**, **Run Browser Checks**, **Cancel**, **Results**, plus **Start Environment** / **Stop** and the "allow tests on the project's database (unsafe)" checkbox. The live base URL is shown next to Start/Stop.
- The dashboard uses a dark card layout with shared button, input, dropdown, and checkbox styles, plus detected-tool chips.
- **Test account row:** dropdown of detected accounts, email box, password box, **Remember**, **Forget**. A `PasswordBox` cannot be bound directly, so the code-behind syncs it with `MainViewModel.AccountPassword` in both directions.
- **Activity log:** a read-only `TextBox` (real text selection, Ctrl+A/Ctrl+C), **Copy all** (copies selection if any), **Clear**, and auto-scroll that only follows output when you're already at the bottom.
- Code-behind mirrors the ViewModel's `Activity` collection into the text box. The earlier `ListBox` version crashed (`ItemsControl is inconsistent with its items source`) and was replaced.

#### `ResultsWindow.xaml` / `ResultsWindow.xaml.cs`
**Function:** Dedicated run-history and result-inspection window.
- The left pane lists recent stored runs by project, start time, and status totals. **New run** starts a fresh logical run the next time any results are saved.
- The main pane provides status filters (`Problems`, `All`, and each status), a result grid (status, source, name, HTTP status, duration, message), and a detail area for the selected result, including exception details, metadata, and evidence path.
- The window uses the refreshed dark visual style, with status-colored text and lightly tinted rows for failures, warnings, and blocked results.
- **Refresh** reloads history/results; **Export HTML + JSON** creates the report and opens its HTML file; **Open run folder** opens the selected run directory; **Show evidence file** locates the selected result's evidence JSON.
- On load, the window refreshes history and selects the active run when available. The dashboard activates the existing Results window instead of opening duplicate windows.

#### `ViewModels/ResultsViewModel.cs`
**Function:** Supplies history, filtering, detail, export, and comparison behavior to the Results window.
- Loads up to the store's default 100 recent runs and the selected run's result rows.
- Defaults to **Problems** (Fail, Warning, Blocked); also supports all results or an individual status.
- Compares the selected run with the nearest earlier run for the same project path. Reports new failures, still-failing results, and failures now passing; matching uses source plus result name.
- Builds selected-result detail text and exposes commands to refresh, start a fresh run, export, open the run directory, and locate evidence.
- Export/open errors appear in the view's status text.

#### `ViewModels/MainViewModel.cs`, `MainViewModel.Browser.cs`, `MainViewModel.Controllers.cs`, and `MainViewModel.Migrations.cs`
**Function:** UI state and commands split across a `partial` class; browser, controller, and migration commands live in focused partial files.
- **Commands:** Browse, Scan, StartEnvironment, StopEnvironment, RunTests, DiscoverRoutes, CheckControllers, CheckMigrations, RunHttpChecks, RunBrowserChecks, CancelRun, OpenResults, ForgetAccount.
- **Enable/disable rules:** Start needs a valid Laravel project and no running environment; Stop needs a running environment; Run Tests / Discover Routes / Check Controllers / Check Migrations need a valid project; Run HTTP Checks needs the environment running; everything is disabled while busy; Cancel is enabled only during a run.
- **Account state:** `AccountEmail`, `AccountPassword`, `RememberAccount`, `SelectedAccount`, `DetectedAccounts`. Browsing to a project runs the seeder scan and loads a saved account if one exists.
- **Run HTTP Checks flow:** discover routes if needed → log in once if an email and password are set (only after a successful login is the account saved, if Remember is ticked) → run the checks with that session → log a summary. If the login fails, it continues with guest checks only.
- **Run Browser Checks flow:** discover routes if needed → attempt the existing one-time login when credentials are set → run the bounded Chromium crawl with the resulting cookies or as a guest → save browser results and log classifications, repeated issues, blocked writes, skips, and screenshot count. Cancellation is checked between pages.
- **State:** `DiscoveredRoutes`, `LastHttpResults`, the current run session, and a reference to the Results window while open.
- **Logging:** thread-safe `Log()` marshals to the UI thread and caps the log at 1000 lines. All process output streams in as `[name] line`.
- **Summaries:** test counts and failing names; route breakdown (GET, API, need auth, parameters, safe-to-check count); HTTP classification/skip breakdown; browser classification, repeated-issue and screenshot counts. HTTP and browser summaries include what was not tested.
- Shared `RunCancellableAsync` wrapper handles busy state, cancellation and error logging.
- **Persistence:** after environment startup, native tests, route discovery, login, migrations, HTTP checks, and browser checks, `SaveAsync` records source results; save failures are logged without discarding the run's in-memory output.

---

## 7. How the pieces flow together

```
Browse folder
   ├─ ProjectScanner ──► ProjectInfo ──► ToolDetector (PHP/Composer/Node/NPM)
   └─ SeederScanner ──► SeededAccount list  (+ SecretStore loads the saved account)

Start Environment
   └─ EnvironmentManager
        ├─ pre-flight (vendor/, node_modules/)
        ├─ ProcessManager.Start("laravel")  ← php artisan serve (free port)
        │    └─ ReadinessChecker.WaitForPortAsync
        └─ ProcessManager.Start("vite")     ← npm run dev (ready text in stdout)

Run Tests
   └─ NativeTestRunner ─► ArtisanRunner ─► ProcessManager.RunAsync("php artisan test --log-junit")
        └─ parse JUnit XML ─► TestResult per case

Discover Routes
   └─ RouteScanner ─► ArtisanRunner ─► ProcessManager.RunAsync("route:list --json")
        └─ RouteInfo list (IsGet / HasParameters / IsApi / RequiresAuth)

Check Controllers
   └─ ControllerChecker ─► route actions to controller files
        ├─ resolves App\ namespace mapping from composer.json
        ├─ checks for missing/commented-out methods
        └─ warns for inherited or trait-provided methods

Check Migrations
   └─ MigrationChecker ─► ArtisanRunner ─► "migrate:status" (read-only)
        └─ Pass / Warning (pending) / Fail (no table) / Blocked

Run HTTP Checks
   ├─ AuthLogin (one POST to /login, localhost only) ─► AuthSession (cookies)   [optional]
   │    └─ SecretStore saves the account after a successful login               [if Remember]
   └─ HttpCheckRunner (needs running environment + routes)
        ├─ skip rules (Safe Mode) ─► SKIPPED + reason
        ├─ guest GET checks
        ├─ logged-in GET checks for protected routes (same session)
        ├─ early stop after 3 identical server errors
        └─ classify ─► TestResult (+ redacted evidence on problems)

Run Browser Checks
   └─ BrowserCheckRunner (Playwright Chromium)
        ├─ seed safe GET routes + follow same-origin links
        ├─ reuse login cookies when login succeeds
        ├─ block page-triggered non-GET requests
        ├─ collect browser, network, and server errors (+ screenshots)
        └─ save results under the browser source

Record results
   └─ MainViewModel.SaveAsync(source, results)
      └─ RunSession.RecordAsync
         ├─ create/reuse active run for the project
         ├─ write evidence JSON for Fail / Warning / Blocked
         └─ RunStore.ReplaceSourceAsync (SQLite transaction + updated counters)

View Results
   └─ ResultsViewModel ─► RunStore (history and details)
      ├─ filter and inspect results; compare with prior run
      ├─ ReportExporter ─► report.html + report.json (emails masked)
      └─ open run directory or selected evidence file

Stop / close window
   └─ ProcessManager.StopAllAsync (kills whole process trees, Guardian-owned only)
```

---

## 8. Progress against the master draft

### Roadmap phases (draft §21)

| Phase | Name | Status | Done | Still open |
|---|---|---|---|---|
| 1 | Foundation | ✅ Done | Solution, MVVM, DI, project selector, Laravel detection, PHP/Composer/Node/NPM detection, Serilog, dashboard | Recent-projects list, multiple saved project configs |
| 2 | Environment Engine | ✅ Done | Process manager, `artisan serve`, `npm run dev`, stdout/stderr capture, readiness checks, owned-process tracking, safe cleanup | Windows Job Object (see limitations), custom service commands, queue workers, `guardian.json` |
| 3 | Laravel Engine | ✅ Done | Artisan runner, `php artisan test`, Pest/PHPUnit detection, JUnit parsing, `route:list`, unified `TestResult`, migration status check | Laravel log reader, targeted test runs |
| 4 | HTTP/API Engine | ✅ Done and verified | Safe GET checks, route classification, status/header/body-snippet capture, redirect handling, skip reporting, early stop, logged-in pass | Scenario-based POST/PUT/PATCH/DELETE; API-specific discovery beyond the `api/` prefix; risky public route warning |
| 5 | Browser Engine (Playwright) | 🟡 First slice implemented and verified | Bounded Chromium crawl; safe GET-route/link discovery; login-cookie reuse; page, console, network and server-error checks; failure screenshots; write blocking; similar-page sampling | SPA/Inertia login; query-string-aware crawling; cancellation during a page; screenshot access from Results; forms and interactive workflows |
| 6 | Interactive Testing (forms, auth profiles, authorization) | 🟡 Partly done | Seeded account discovery, encrypted remembered account, one login, logged-in GET checks, role separation (403) seen from the admin role. Verified on `scms`. | Multi-role runs and an allowed/forbidden matrix; forms; safe validation tests; 2FA accounts |
| 7 | Diagnostics and Reporting | 🟡 First slice delivered | SQLite run/result history, per-result evidence JSON, results grid/details, status filters, prior-run failure comparison, HTML/JSON export with email masking. Results window verified. | Verify export after the masking change; retention/cleanup policy, log correlation, richer evidence navigation and report polish |
| 8 | Static Analysis | 🟡 Partly done | Route-to-controller check implemented in-app; concept still needs a real-project validation pass. | Model vs migration columns, validation vs save hints, PHPStan/Larastan, Pint, ESLint/TS |
| 9 | Expectations | ⬜ Not started | | Scenario format and API/browser/DB expectations, baseline snapshots |
| 10 | Advanced / Future | ⬜ Not started | | CLI, CI mode, AI diagnosis, parallel runs |

### MVP checklist (draft §22)

| MVP step | Status |
|---|---|
| Select project | ✅ |
| Detect Laravel | ✅ |
| Detect PHP / Composer / Node | ✅ |
| Start Laravel | ✅ |
| Start Vite if enabled | ✅ |
| Wait until ready | ✅ |
| Run `php artisan test` | ✅ |
| Run route discovery | ✅ |
| Run safe HTTP health checks | ✅ verified on `scms` |
| Run checks with a seeded test account | ✅ verified on `scms` (admin) |
| Run browser checks | ✅ first slice verified on `scms` (login cookies, known 500 pages, screenshots, deduplication, similar-page sampling) |
| Show results | ✅ results grid, filters, details, history, export |
| Stop Guardian-owned processes | ✅ |

### Draft principles honoured so far

- Orchestrator, not a replacement for Pest/PHPUnit.
- Honest statuses (`Blocked` and `Skipped` instead of fake passes); an early stop reports the unchecked routes as `Blocked`, not as passed.
- Safe Mode by default: no state-changing requests, risky GET paths skipped. Browser pages are restricted to safe navigation and page-triggered writes are blocked; the only POST is the existing login request, and only on localhost.
- Only Guardian-owned processes are killed.
- Structured results instead of exceptions leaking into the UI.
- Reports what was *not* tested, not just what was.
- Results, evidence, and exports are persisted locally so findings survive app restarts.
- Passwords are never logged or put in results; remembered passwords are encrypted per Windows user; exported reports mask emails.
- Guardian never modifies the tested project and never runs `migrate`.

---

## 9. Real-world findings so far (from `scms`)

See section 2 for the current list of bugs found. Background facts from setting up the target:

| Finding | Meaning |
|---|---|
| 167 routes: 96 GET, 0 API, 151 need auth, 67 with parameters | Guest-only coverage was 9 routes; with the admin login 59 routes are checked. 71 non-GET routes and 31 routes with parameters stay untested. |
| Initial 24/25 native test failures were `could not find driver (sqlite)` | Environment issue (PHP `pdo_sqlite`/`sqlite3` extensions disabled), fixed outside Guardian. |
| First HTTP runs failed with `Unknown database 'scms'` | Environment issue (database not created). Led to the early-stop feature and readable error messages. |
| Vite reported ports 5173/5174 in use | Probably orphaned `node.exe` processes from an earlier crash or a second Vite instance. |
| PHP built-in server: `forking is not supported on this platform` | On Windows `artisan serve` handles one request at a time, so the HTTP engine deliberately sends requests sequentially. Every page takes about 500 ms for this reason. |
| Pages that return 500 take about 10 s | The app's error page rendering is slow; one reason for the early stop. |
| Browser checks on `scms` | The browser crawl transferred the login session, found known 500 pages with screenshots, deduplicated repeated console/network issues, and limited similar pages by URL pattern. |

---

## 10. Known limitations and technical debt

1. **Crash orphaning:** if Guardian itself crashes hard, child `php.exe`/`node.exe` processes can survive. Fix: a Windows Job Object that kills children when the parent dies.
2. **DB-safety check is a heuristic** (see `NativeTestRunner`).
3. **Evidence is persisted.** HTTP evidence and result metadata are written to per-result JSON files and shown in the detail view; nothing currently expires automatically. Evidence files and the SQLite history keep real emails; only exported reports are masked.
4. **Redaction is best-effort.** It masks cookies, tokens, passwords and similar patterns, but a debug error page or result metadata could still expose project data. Inspect evidence before sharing it.
5. **Laravel version shows the constraint** (`^12.0`), not the installed version. Could be read from `composer.lock` or `php artisan --version`.
6. **Tool version strings are noisy** (PHP shows build date and compiler). Cosmetic.
7. **Options are hard-coded.** `HttpCheckOptions` uses defaults; there is no UI or `guardian.json` yet.
8. **Run history has no retention/cleanup policy.** The SQLite database, evidence files, and exports can grow indefinitely; there is no UI to delete runs.
9. **Runs are source-aggregates, not immutable execution snapshots.** Re-running a source replaces its prior results in the active run, so environment, test, route, migration, login and HTTP checks can represent different moments. `CurrentRunId` is in memory and is not restored after app restart.
10. **`MainViewModel` is still large.** The migrations command already lives in a partial file; project/environment/run orchestration remains together.
11. **The auth workflow is heuristic.** `SeederScanner` reads literal values only (not `env()` or random factory values). `AuthLogin` assumes a standard `/login` form with a CSRF token and the field names `email` and `password`; it cannot log in to accounts with two-factor authentication or SPA-style logins.
12. **A logged-in 403 counts as a pass.** This hides a role that is configured too strictly. The count is still visible in the Breakdown line (`forbidden-for-role`).
13. **The early stop is a heuristic.** Three identical errors in a row stop the run; routes after that are reported as `Blocked`, so a real second bug behind the first can be missed until the first is fixed.
14. **The migrations check needs the project's database to be reachable** (MySQL running), but not the Guardian environment.
15. **Activity log is capped at 1000 lines** (set in `MainViewModel.Log`). Large test suites scroll past it.
16. **Test project is empty.** `LaravelGuardian.Tests` still has only the default xUnit placeholder. Good first candidates: `ProjectScanner`, `RouteInfo` helpers, JUnit parsing (including the message cleanup), `HttpCheckRunner` skip rules and classification, `SeederScanner`, `RunComparer`, and `RunStore` source replacement/counts.
17. **Comparison is name-based.** `RunComparer` keys on source plus result name; duplicate names collapse to the last result and renames appear as unrelated new/fixed results.
18. **Redirects to another origin are not followed.** If the app forces `APP_URL`, redirects may point at a different server and are reported as `redirect-external`.
19. **Coverage is still scoped.** HTTP checks cover safe GET routes without parameters, one account at a time. Browser checks add bounded same-origin page crawling, but do not submit forms, exercise controls, or cover multiple roles; broader coverage remains open.
20. **Browser login is not general-purpose.** It reuses the existing standard `/login` flow and cookie session; SPA/Inertia-style login is not implemented, so those projects currently fall back to guest pages.
21. **Browser crawl normalizes URLs.** Query strings are dropped while de-duplicating pages, so distinct query-driven states are not checked. Similar numeric/GUID paths are sampled rather than exhaustively visited.
22. **Browser cancellation is page-granular.** Cancellation takes effect between pages, not necessarily while an in-progress navigation or page wait is running.
23. **Screenshot presentation needs confirmation.** Screenshot paths are saved to results/evidence; verify that users can access them directly from the Results window.

---

## 11. Immediate next actions

1. **Fix the `scms` findings and run again** (your side): add or remove the missing controller methods, remove `Features::registration()`, deal with the `ConfirmPasswordViewResponse` routes, protect or remove `/debug/google-config`. Expect the HTTP run to reach 59 of 59 passed.
2. **Verify the `UserController::update()` fix by hand:** edit a student with details filled in, save without changes, reopen. The details must still be there.
3. **Verify report and screenshot access:** Results → Export HTML + JSON should mask emails and mention logged-in routes; also confirm browser screenshots can be opened from Results.
4. **Validate the route-to-controller check** against a live Laravel project; it is implemented and would have found the missing methods without opening a page.
5. **Update the stale native tests in `scms`** (`RegistrationTest`, the `/dashboard` redirect, `name` vs first and last name).
6. **Reliability pass:** Windows Job Object, first tests in `LaravelGuardian.Tests`, run retention.
7. **Choose the next feature slice:** close browser gaps (Phase 5), add more Phase 8 checks (model vs migration columns, validation vs save hints), or implement multi-role runs (Phase 6).

---

## 12. Quick reference: running Guardian

```powershell
cd F:\Projects\LaravelGuardian
dotnet clean
dotnet run --project src/LaravelGuardian.UI
```

Typical session: **Browse** to a Laravel project (the seeder scan suggests a test account) → **Start Environment** → **Discover Routes** → **Check Migrations** → **Run Tests** → **Run HTTP Checks** (logs in once if an account is set) → **Results** to review/export → **Stop** (or just close the window).

Logs: `%LocalAppData%\LaravelGuardian\logs\`
Run data: `%LocalAppData%\LaravelGuardian\guardian.db` and `...\runs\`
Saved accounts: `%LocalAppData%\LaravelGuardian\accounts.json`
# Laravel Guardian

Laravel Guardian is a desktop utility for validating Laravel projects quickly and safely from a local Windows environment. It scans a Laravel app, starts a local environment, runs native tests, inspects routes, performs guest-safe HTTP checks, and stores the results in a local database with exportable reports.

This project is designed to help developers answer a simple question quickly: “Is my Laravel project healthy enough to serve, route, and respond as expected?”

## Overview

Laravel Guardian combines a small WPF interface with a service layer that:

- detects whether a folder is a valid Laravel project,
- verifies the local toolchain (PHP, Composer, Node, NPM),
- starts a Laravel development server,
- optionally starts Vite for frontend dev assets,
- discovers routes through `php artisan route:list --json`,
- runs native tests via `php artisan test`,
- checks GET routes with HTTP probes in safe mode,
- records run history and generates HTML/JSON reports.

## Key Features

### 1. Laravel project detection

The project scanner inspects the folder for common Laravel indicators:

- `artisan`
- `composer.json`
- standard Laravel directories such as `app`, `routes`, `public`, and `storage`

It also reads Composer and package metadata to detect:

- Laravel framework version
- Pest or PHPUnit usage
- Vite usage
- React/Vue frontend framework hints

### 2. Environment startup

The app can start the Laravel app locally with:

- `php artisan serve --host=127.0.0.1 --port=<random>`

If the project also has Vite, it can optionally launch:

- `npm run dev`

The environment manager waits for the relevant ports and flags readiness before continuing.

### 3. Native test execution

Laravel Guardian runs Laravel’s native test suite using `php artisan test` and parses the JUnit XML output.

It adds guardrails to reduce risk:

- blocks execution if `vendor/` is missing,
- warns when a safe database setting is not configured,
- rejects unsafe runs unless the user explicitly allows them,
- records pass/fail/warning/skipped/blocked states.

### 4. Route discovery

The route scanner executes:

- `php artisan route:list --json --no-ansi`

It parses the JSON output and captures route metadata such as:

- HTTP methods
- URI path
- route name
- action
- domain
- middleware

This information is used to determine which routes are safe to probe in a guest-only check mode.

### 5. Safe HTTP validation

The HTTP checker performs GET-only, guest-mode checks against discovered routes. It is intentionally conservative to avoid destructive or authenticated flows.

During safe mode, it skips routes that:

- require authentication,
- have parameters,
- are not GET requests,
- are domain-bound,
- appear to be destructive or risky,
- are duplicates or beyond the configured route limit.

It evaluates:

- HTTP status codes
- redirects and redirect loops
- JSON validity on API-style responses
- slow responses
- 401/403/404/429/500 handling
- HTML returned by an API endpoint
- exception patterns in error pages

### 6. Persistent run history and reporting

Results are stored in SQLite using a local run store. Each project run captures:

- project metadata,
- total/passed/failed/warning/skipped/blocked counts,
- source-specific results from tests, routes, and HTTP checks,
- evidence files for failed or blocked results.

The app can also export HTML and JSON reports for each run.

## Tech Stack

- .NET 10
- WPF (Windows desktop UI)
- CommunityToolkit.Mvvm
- Microsoft.Data.Sqlite
- Serilog
- PHP / Laravel project integration

## Project Structure

```text
LaravelGuardian/
├── LaravelGuardian.slnx
├── README.md
├── src/
│   ├── LaravelGuardian.Core/
│   │   ├── Interfaces/
│   │   ├── Models/
│   │   └── ...
│   ├── LaravelGuardian.Services/
│   │   ├── EnvironmentManager.cs
│   │   ├── ArtisanRunner.cs
│   │   ├── HttpCheckRunner.cs
│   │   ├── NativeTestRunner.cs
│   │   ├── ProjectScanner.cs
│   │   ├── RouteScanner.cs
│   │   ├── ReportExporter.cs
│   │   ├── RunStore.cs
│   │   └── ...
│   └── LaravelGuardian.UI/
│       ├── ViewModels/
│       ├── MainWindow.xaml
│       ├── ResultsWindow.xaml
│       └── ...
└── tests/
    └── LaravelGuardian.Tests/
```

## Requirements

Before running the app, make sure the following are installed:

- .NET 10 SDK
- PHP
- Composer
- Node.js and NPM
- A valid Laravel project on disk

## Getting Started

### 1. Restore dependencies

```bash
dotnet restore
```

### 2. Build the solution

```bash
dotnet build
```

### 3. Run the UI

```bash
dotnet run --project src/LaravelGuardian.UI/LaravelGuardian.UI.csproj
```

### 4. Use the app

1. Choose a Laravel project folder.
2. Let the project scanner validate the app.
3. Start the environment.
4. Run native tests if the project includes Pest or PHPUnit.
5. Discover routes.
6. Run HTTP checks in safe mode.
7. Review the results and exported run reports.

## Typical Workflow

A common validation flow looks like this:

1. Detect and confirm the Laravel project.
2. Start the application locally.
3. Run `php artisan test` to capture native failures.
4. Run route discovery to map the app surface.
5. Send GET-only checks to each safe route.
6. Review HTML/JSON reports for patterns and regressions.

## Safety and Limitations

This tool intentionally avoids unsafe or destructive requests by default.

Important notes:

- HTTP checks run as a guest and only hit GET routes in safe mode.
- Authenticated routes and parameterized routes are skipped unless a later workflow introduces test accounts.
- Database-sensitive tests require explicit handling to avoid wiping a shared dev database.
- The tool is designed for local validation and diagnostics, not as a full production security or QA harness.

## Output and Reporting

The app surfaces a live activity log while checks run and stores a history of runs locally. Each run can be exported to:

- HTML summary report
- JSON machine-readable output

This makes it easier to compare current issues with prior results and track regressions over time.

## License

This project does not currently declare a specific license in the repository. If a license is required for your environment, add the appropriate license file before distribution.

## Notes

This README is based on the current implementation and project structure present in the repository. If a PDF specification or a formal product brief is added later, it can be used to expand this document with exact product requirements, screenshots, release notes, and architecture details.

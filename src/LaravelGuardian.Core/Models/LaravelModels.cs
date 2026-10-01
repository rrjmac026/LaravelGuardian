namespace LaravelGuardian.Core.Models;

public class CommandResult
{
    public string Command { get; set; } = "";
    public bool Started { get; set; } = true;
    public int ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public TimeSpan Duration { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
}

public class RouteInfo
{
    public List<string> Methods { get; set; } = new();
    public string Uri { get; set; } = "";
    public string? Name { get; set; }
    public string? Action { get; set; }
    public string? Domain { get; set; }
    public List<string> Middleware { get; set; } = new();

    public bool IsGet => Methods.Contains("GET");
    public bool HasParameters => Uri.Contains('{');
    public bool IsApi => Uri == "api" || Uri.StartsWith("api/", StringComparison.OrdinalIgnoreCase);
    public bool RequiresAuth => Middleware.Any(m =>
        m.Equals("auth", StringComparison.OrdinalIgnoreCase) ||
        m.StartsWith("auth:", StringComparison.OrdinalIgnoreCase) ||
        m.Contains("Authenticate", StringComparison.OrdinalIgnoreCase));
}

public class RouteScanResult
{
    public TestResult Result { get; set; } = new();
    public List<RouteInfo> Routes { get; set; } = new();
}
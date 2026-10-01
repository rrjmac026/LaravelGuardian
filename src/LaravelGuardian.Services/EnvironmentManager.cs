using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

public class EnvironmentManager : IEnvironmentManager
{
    private readonly IProcessManager _processes;
    private TaskCompletionSource<bool>? _viteReady;

    public EnvironmentManager(IProcessManager processes)
    {
        _processes = processes;
        _processes.OutputReceived += OnOutput;
    }

    public string? BaseUrl { get; private set; }
    public bool IsRunning => _processes.IsRunning("laravel");

    private void OnOutput(string name, string line, bool isError)
    {
        if (name == "vite" && (line.Contains("ready in") || line.Contains("Local:")))
            _viteReady?.TrySetResult(true);
    }

    public async Task<IReadOnlyList<TestResult>> StartAsync(
        ProjectInfo project, bool startVite, CancellationToken ct = default)
    {
        var results = new List<TestResult>();

        // ---------- Laravel ----------
        var laravel = new TestResult { Category = "Environment", Name = "Laravel server" };
        var sw = Stopwatch.StartNew();
        results.Add(laravel);

        if (!File.Exists(Path.Combine(project.Path, "vendor", "autoload.php")))
        {
            Block(laravel, "vendor/ is missing. Run 'composer install' in the project first.");
            return results;
        }

        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";

        try
        {
            _processes.Start("laravel", "php",
                $"artisan serve --host=127.0.0.1 --port={port} --no-reload", project.Path);
        }
        catch (Exception ex)
        {
            BaseUrl = null;
            Block(laravel, $"Could not start PHP: {ex.Message}");
            return results;
        }

        var ready = await ReadinessChecker.WaitForPortAsync(
            "127.0.0.1", port, TimeSpan.FromSeconds(90), () => _processes.IsRunning("laravel"), ct);

        laravel.Duration = sw.Elapsed;
        if (!ready)
        {
            await StopAsync();
            Block(laravel, "Laravel server did not start listening within 90s (it crashed or is booting very slowly). Check the activity log.");
            return results;
        }

        laravel.Status = TestStatus.Pass;
        laravel.Url = BaseUrl;
        laravel.Message = $"Ready at {BaseUrl}";

        // ---------- Vite ----------
        if (startVite)
        {
            var vite = new TestResult { Category = "Environment", Name = "Vite server" };
            results.Add(vite);
            sw.Restart();

            if (!Directory.Exists(Path.Combine(project.Path, "node_modules")))
            {
                Block(vite, "node_modules/ is missing. Run 'npm install' in the project first.");
                return results;
            }

            _viteReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _processes.Start("vite", "cmd.exe", "/c npm run dev", project.Path);
            }
            catch (Exception ex)
            {
                Block(vite, $"Could not start npm: {ex.Message}");
                return results;
            }

            var viteUp = await WaitForViteAsync(TimeSpan.FromSeconds(30), ct);
            vite.Duration = sw.Elapsed;

            if (viteUp)
            {
                vite.Status = TestStatus.Pass;
                vite.Message = "Vite dev server ready";
            }
            else if (!_processes.IsRunning("vite"))
            {
                Block(vite, "Vite exited right after starting. Check the activity log.");
            }
            else
            {
                vite.Status = TestStatus.Warning;
                vite.Severity = Severity.Medium;
                vite.Message = "Vite is running but did not report ready within 30s.";
            }
        }

        return results;
    }

    public async Task StopAsync()
    {
        await _processes.StopAllAsync();
        BaseUrl = null;
    }

    private async Task<bool> WaitForViteAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_viteReady!.Task.IsCompleted) return true;
            if (!_processes.IsRunning("vite")) return false;
            await Task.Delay(250, ct);
        }
        return false;
    }

    private static void Block(TestResult r, string message)
    {
        r.Status = TestStatus.Blocked;
        r.Severity = Severity.High;
        r.Message = message;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
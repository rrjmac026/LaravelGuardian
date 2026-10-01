using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

public class ProcessManager : IProcessManager
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    private int _sequence;

    public event Action<string, string, bool>? OutputReceived;
    public event Action<string, int>? ProcessExited;

    private static ProcessStartInfo CreateStartInfo(string fileName, string arguments, string workingDirectory) =>
        new(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

    public int Start(string name, string fileName, string arguments, string workingDirectory)
    {
        var process = new Process
        {
            StartInfo = CreateStartInfo(fileName, arguments, workingDirectory),
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) OutputReceived?.Invoke(name, Ansi.Replace(e.Data, ""), false);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) OutputReceived?.Invoke(name, Ansi.Replace(e.Data, ""), true);
        };
        process.Exited += (_, _) =>
        {
            int code = -1;
            try { code = process.ExitCode; } catch { }
            _processes.TryRemove(name, out _);
            ProcessExited?.Invoke(name, code);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes[name] = process;
        return process.Id;
    }

    public async Task<CommandResult> RunAsync(
        string name, string fileName, string arguments, string workingDirectory,
        TimeSpan timeout, bool streamOutput = true, CancellationToken ct = default)
    {
        var result = new CommandResult { Command = $"{fileName} {arguments}" };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var key = $"{name}#{Interlocked.Increment(ref _sequence)}";

        using var process = new Process { StartInfo = CreateStartInfo(fileName, arguments, workingDirectory) };

        void Handle(string? data, StringBuilder sink, bool isError)
        {
            if (data is null) return;
            var clean = Ansi.Replace(data, "");
            lock (sink) sink.AppendLine(clean);
            if (streamOutput) OutputReceived?.Invoke(name, clean, isError);
        }

        process.OutputDataReceived += (_, e) => Handle(e.Data, stdout, false);
        process.ErrorDataReceived += (_, e) => Handle(e.Data, stderr, true);

        var sw = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            result.Started = false;
            result.ExitCode = -1;
            result.Stderr = ex.Message;
            return result;
        }

        _processes[key] = process;
        try
        {
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                process.WaitForExit(5000);
                if (ct.IsCancellationRequested) throw;
                result.TimedOut = true;
            }

            result.ExitCode = process.HasExited ? process.ExitCode : -1;
        }
        finally
        {
            _processes.TryRemove(key, out _);
        }

        result.Duration = sw.Elapsed;
        lock (stdout) result.Stdout = stdout.ToString();
        lock (stderr) result.Stderr = stderr.ToString();
        return result;
    }

    public bool IsRunning(string name)
    {
        try { return _processes.TryGetValue(name, out var p) && !p.HasExited; }
        catch { return false; }
    }

    public async Task StopAllAsync()
    {
        foreach (var process in _processes.Values.ToArray())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(cts.Token);
                }
            }
            catch { /* already gone or timed out */ }
        }
        _processes.Clear();
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
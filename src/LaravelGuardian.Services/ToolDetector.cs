// ToolDetector.cs
using System.Diagnostics;
using LaravelGuardian.Core.Interfaces;
using LaravelGuardian.Core.Models;

namespace LaravelGuardian.Services;

public class ToolDetector : IToolDetector
{
    public async Task<ToolInfo> DetectAsync(string name, string command, string args, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {command} {args}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);

            if (p.ExitCode != 0) return new ToolInfo(name, false, null);
            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return new ToolInfo(name, true, firstLine);
        }
        catch
        {
            return new ToolInfo(name, false, null);
        }
    }
}
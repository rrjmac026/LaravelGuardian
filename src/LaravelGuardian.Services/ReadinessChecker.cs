using System.Net.Sockets;

namespace LaravelGuardian.Services;

public static class ReadinessChecker
{
    /// Plain TCP connect: proves the port is listening without running any app code.
    public static async Task<bool> WaitForPortAsync(
        string host, int port, TimeSpan timeout, Func<bool> processAlive, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!processAlive()) return false;

            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(1));
                await client.ConnectAsync(host, port, cts.Token);
                return true;
            }
            catch (SocketException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

            await Task.Delay(300, ct);
        }
        return false;
    }
}
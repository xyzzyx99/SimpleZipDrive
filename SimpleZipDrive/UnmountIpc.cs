using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace SimpleZipDrive;

/// <summary>Per-mounted-volume local IPC for --unmount. No driver API is needed.</summary>
internal sealed class UnmountIpc : IDisposable
{
    private CancellationTokenSource? _stop;
    private string? _activePoint;

    private static string PipeName(string path)
    {
        var normalized = Normalize(path);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "SimpleZipDrive.Unmount." + Convert.ToHexString(bytes.AsSpan(0, 12));
    }

    private static string Normalize(string path)
    {
        path = path.Trim().Trim('"').TrimEnd('\\', '/');
        if (path.Length > 0 && char.IsAsciiLetter(path[0]) &&
            (path.Length == 1 || (path.Length == 2 && path[1] == ':')))
            return char.ToUpperInvariant(path[0]) + ":";
        return Path.GetFullPath(path).TrimEnd('\\', '/').ToUpperInvariant();
    }

    internal static async Task<bool> SendAsync(string mountPoint)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName(mountPoint),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("UNMOUNT");
        var response = await reader.ReadLineAsync(timeout.Token);
        return response == "OK";
    }

    internal void Start(string mountPoint, Func<Task<bool>> unmount)
    {
        var normalized = Normalize(mountPoint);
        if (_activePoint == normalized) return;
        Stop();
        _activePoint = normalized;
        _stop = new CancellationTokenSource();
        _ = ListenAsync(PipeName(normalized), unmount, _stop.Token);
    }

    private static async Task ListenAsync(string name, Func<Task<bool>> unmount, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                var request = await reader.ReadLineAsync(stop);
                if (request != "UNMOUNT")
                {
                    await writer.WriteLineAsync("ERROR");
                    continue;
                }
                // ACK while the connection is still alive. UnmountAsync changes
                // the mount status, which stops this listener and otherwise can
                // race with sending the reply. ACK means request accepted.
                await writer.WriteLineAsync("OK");
                await unmount();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception) when (!stop.IsCancellationRequested) { await Task.Delay(200, stop); }
        }
    }

    internal void Stop()
    {
        _activePoint = null;
        var previous = _stop;
        _stop = null;
        if (previous is null) return;
        previous.Cancel();
        // Do not dispose immediately: the background accept loop still owns its token.
    }

    public void Dispose() => Stop();
}

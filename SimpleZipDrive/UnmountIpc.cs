using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace SimpleZipDrive;

/// <summary>Per-mount, current-user IPC endpoint for the --unmount command.</summary>
internal sealed class UnmountIpc : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private CancellationTokenSource? _stop;
    private string? _activePoint;

    private static string Normalize(string path)
    {
        path = path.Trim().Trim('"').TrimEnd('\\', '/');
        if (path.Length > 0 && char.IsAsciiLetter(path[0]) &&
            (path.Length == 1 || (path.Length == 2 && path[1] == ':')))
            return char.ToUpperInvariant(path[0]) + ":";
        return Path.GetFullPath(path).TrimEnd('\\', '/').ToUpperInvariant();
    }

    private static string PipeName(string mountPoint)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(mountPoint)));
        return "SimpleZipDrive.Unmount." + Convert.ToHexString(hash.AsSpan(0, 12));
    }

    internal static async Task<bool> SendAsync(string mountPoint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var pipe = new NamedPipeClientStream(".", PipeName(mountPoint),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var writer = new StreamWriter(pipe, Utf8NoBom, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Utf8NoBom, leaveOpen: true);
        await writer.WriteLineAsync("UNMOUNT".AsMemory(), timeout.Token).ConfigureAwait(false);
        var answer = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        return answer == "OK";
    }

    internal void Start(string mountPoint, Func<Task<bool>> unmount)
    {
        var normalized = Normalize(mountPoint);
        if (_activePoint == normalized && _stop is { IsCancellationRequested: false }) return;
        Stop();
        _activePoint = normalized;
        _stop = new CancellationTokenSource();
        _ = ListenAsync(PipeName(normalized), unmount, _stop.Token);
    }

    private static async Task ListenAsync(string name, Func<Task<bool>> unmount,
        CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Utf8NoBom, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Utf8NoBom, leaveOpen: true)
                    { AutoFlush = true };
                var request = await reader.ReadLineAsync(stop).ConfigureAwait(false);
                if (request != "UNMOUNT")
                {
                    await writer.WriteLineAsync("ERROR".AsMemory(), stop).ConfigureAwait(false);
                    continue;
                }

                // ACK before dispatching the potentially blocking filesystem unmount.
                await writer.WriteLineAsync("OK".AsMemory(), stop).ConfigureAwait(false);
                await writer.FlushAsync(stop).ConfigureAwait(false);

                // Never wait for unmount in the pipe request loop. It can cancel
                // this very listener via MountStatusChanged -> UpdateMountStatus.
                _ = Task.Run(async () =>
                {
                    try { await unmount().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine($"IPC unmount failed: {ex}");
                    }
                });
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                System.Diagnostics.Trace.WriteLine($"Unmount IPC listener: {ex}");
                try { await Task.Delay(200, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    internal void Stop()
    {
        _activePoint = null;
        var token = _stop;
        _stop = null;
        token?.Cancel();
    }

    public void Dispose() => Stop();
}

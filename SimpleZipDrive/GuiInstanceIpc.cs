using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;

namespace SimpleZipDrive;

// One host per Windows user and interactive session. Clients do not initialize Avalonia.
internal sealed class GuiInstanceIpc : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<string[]> _dispatch;
    internal static string InstanceName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            return $"SimpleZipDrive.MultiMount.{identity.User!.Value}.{process.SessionId}";
        }
    }
    internal GuiInstanceIpc(Action<string[]> dispatch)
    {
        _dispatch = dispatch;
        _ = ListenAsync(_stop.Token);
    }

    internal static string[] NormalizeArgs(string[] args)
    {
        var result = args.ToArray();
        var positional = 0;
        for (var i = 0; i < result.Length; ++i)
        {
            if (result[i].Equals("--show", StringComparison.OrdinalIgnoreCase)) continue;
            var value = result[i].Trim().Trim('"');
            if (positional == 0) result[i] = Path.GetFullPath(value);
            else if (positional == 1)
            {
                var p = value.TrimEnd('\\', '/');
                result[i] = p.Length >= 1 && char.IsAsciiLetter(p[0]) &&
                    (p.Length == 1 || (p.Length == 2 && p[1] == ':'))
                    ? char.ToUpperInvariant(p[0]) + ":" : Path.GetFullPath(value);
            }
            positional++;
        }
        return result;
    }

    internal static async Task<bool> SignalAsync(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(NormalizeArgs(args)).AsMemory(), timeout.Token)
            .ConfigureAwait(false);
        return string.Equals(await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false),
            "OK", StringComparison.Ordinal);
    }

    private async Task ListenAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(InstanceName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                var args = line is null ? null : JsonSerializer.Deserialize<string[]>(line);
                if (args is null || args.Length > 3 || args.Any(a => a is null))
                {
                    await writer.WriteLineAsync("ERROR".AsMemory(), deadline.Token).ConfigureAwait(false);
                    continue;
                }
                // ACK means queued, not that the filesystem has mounted.
                Dispatcher.UIThread.Post(() => _dispatch(args), DispatcherPriority.Normal);
                await writer.WriteLineAsync("OK".AsMemory(), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Trace.WriteLine($"Multi-mount listener: {ex}");
                if (stop.IsCancellationRequested) return;
            }
        }
    }
    public void Dispose() => _stop.Cancel();
}

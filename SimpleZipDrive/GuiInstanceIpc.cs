using System.IO.Pipes;
using System.Text;
using Avalonia.Threading;

namespace SimpleZipDrive;

/// <summary>
/// Lets a second no-argument launch open the already running GUI instance.
/// Mount CLI launches intentionally remain separate until multi-mount support
/// is implemented in the main application.
/// </summary>
internal sealed class GuiInstanceIpc : IDisposable
{
    private const string PipeName = "SimpleZipDrive.Gui.SingleInstance";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Action _show;

    internal GuiInstanceIpc(Action show)
    {
        _show = show;
        _ = ListenAsync(_stop.Token);
    }

    internal static async Task<bool> SignalShowAsync()
    {
        // Another instance might hold the mutex but still be initializing its UI.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName,
                    PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(300, timeout.Token).ConfigureAwait(false);
                using var writer = new StreamWriter(pipe, Utf8NoBom, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Utf8NoBom, leaveOpen: true);
                await writer.WriteLineAsync("SHOW".AsMemory(), timeout.Token).ConfigureAwait(false);
                var answer = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                return string.Equals(answer, "OK", StringComparison.Ordinal);
            }
            catch (TimeoutException) { }
            catch (IOException) { }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { break; }
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
        return false;
    }

    private async Task ListenAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var writer = new StreamWriter(pipe, Utf8NoBom, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Utf8NoBom, leaveOpen: true);
                var request = await reader.ReadLineAsync(stop).ConfigureAwait(false);
                if (string.Equals(request, "SHOW", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("OK".AsMemory(), stop).ConfigureAwait(false);
                    Dispatcher.UIThread.Post(_show, DispatcherPriority.Normal);
                }
                else
                {
                    await writer.WriteLineAsync("ERROR".AsMemory(), stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                System.Diagnostics.Trace.WriteLine($"Single-instance GUI listener: {ex}");
                try { await Task.Delay(150, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}

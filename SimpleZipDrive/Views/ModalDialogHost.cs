using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace SimpleZipDrive.Views;

/// <summary>
///     Hosts the application's synchronous modal dialogs (message boxes and password prompts)
///     on the Avalonia UI thread.
/// </summary>
/// <remarks>
///     The dialogs keep a synchronous API because they are raised from backend filesystem
///     callbacks. The UI thread runs a nested dispatcher loop until the dialog closes;
///     concurrent callers from background threads are serialized by a gate acquired before the
///     dispatcher is entered, so two dialogs never nest their loops (which would leave the
///     first caller blocked until the second dialog closes as well).
/// </remarks>
internal static class ModalDialogHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    ///     Creates and shows a modal dialog, blocking until it is closed.
    /// </summary>
    /// <typeparam name="T">The dialog result type.</typeparam>
    /// <param name="createDialog">Factory that creates the dialog (runs on the UI thread).</param>
    /// <param name="readResult">Reads the result from the closed dialog (runs on the UI thread).</param>
    /// <returns>The value returned by <paramref name="readResult" />.</returns>
    public static T Show<T>(Func<Window> createDialog, Func<Window, T> readResult)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            // Acquire the gate on the calling thread, before entering the UI thread, so a
            // second background prompt waits instead of nesting a dispatcher loop.
            Gate.Wait();
            try
            {
                return Dispatcher.UIThread.Invoke(() => Run(createDialog, readResult));
            }
            finally
            {
                Gate.Release();
            }
        }

        // Already on the UI thread (for example a dialog raised from a UI event handler):
        // never block on the gate, because another modal dialog may own the nested loop.
        if (!Gate.Wait(0))
            return Run(createDialog, readResult);

        try
        {
            return Run(createDialog, readResult);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    ///     Returns a visible, non-minimized owner for a modal dialog. A hidden tray window
    ///     cannot own ShowDialog: Avalonia throws if the owner is not visible.
    /// </summary>
    /// <returns>A visible owner, or <see langword="null" /> in tray-only mode.</returns>
    public static Window? GetOwnerWindow()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (lifetime is null)
            return null;

        var active = lifetime.Windows.LastOrDefault(static window =>
            window.IsActive && window.IsVisible && window.WindowState != WindowState.Minimized);
        if (active is not null)
            return active;

        var main = lifetime.MainWindow;
        if (main is { IsVisible: true } && main.WindowState != WindowState.Minimized)
            return main;

        return lifetime.Windows.LastOrDefault(static window =>
            window.IsVisible && window.WindowState != WindowState.Minimized);
    }

    private static T Run<T>(Func<Window> createDialog, Func<Window, T> readResult)
    {
        if (!Dispatcher.UIThread.SupportsRunLoops)
            throw new InvalidOperationException("Modal dialogs require a desktop application lifetime.");

        var dialog = createDialog();
        var owner = GetOwnerWindow();

        var closed = new CancellationTokenSource();
        var closeHandler = new DialogCloseHandler(closed);

        dialog.Closed += closeHandler.OnClosed;

        try
        {
            if (owner is not null && owner != dialog)
            {
                _ = dialog.ShowDialog(owner);
            }
            else
            {
                // Tray startup has no visible main window. The prompt must be shown
                // without an owner, with its own taskbar presence and foreground focus.
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.ShowInTaskbar = true;
                dialog.Topmost = true;
                dialog.Show();
                dialog.Activate();
            }

            // Nested dispatcher loop: keeps the modal dialog responsive while the calling
            // (possibly background) thread blocks until the user closes it. The token is
            // cancelled from the Closed handler, so the loop cannot outlive the dialog.
            Dispatcher.UIThread.MainLoop(closed.Token);

            return readResult(dialog);
        }
        finally
        {
            dialog.Closed -= closeHandler.OnClosed;
            closed.Dispose();
        }
    }

    /// <summary>
    ///     Cancels the nested-loop token when the dialog closes. A handler object is used instead
    ///     of a lambda so the disposable token source is not captured by a closure that outlives
    ///     the method scope.
    /// </summary>
    private sealed class DialogCloseHandler(CancellationTokenSource closed)
    {
        public void OnClosed(object? sender, EventArgs e)
        {
            closed.Cancel();
        }
    }
}

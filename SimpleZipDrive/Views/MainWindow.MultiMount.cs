using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace SimpleZipDrive.Views;

public partial class MainWindow
{
    private sealed class MountSession(MountService service, string archive, string? requestedPoint)
    {
        public MountService Service { get; } = service;
        public string Archive { get; } = archive;
        public string? RequestedPoint { get; } = requestedPoint;
        public string? Point { get; set; }
        public UnmountIpc Ipc { get; } = new();
        public MountProgressWindow? Progress { get; set; }
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Lifetime { get; set; } = Task.CompletedTask;
        public Task? StopTask { get; set; }
        public bool Stopping { get; set; }
        public bool Mounted => Point is not null && !Stopping && Service.IsMounted;
        public string Label => $"{Point ?? RequestedPoint ?? "Auto"} — {Path.GetFileName(Archive)}";
    }

    // All session state belongs to the UI thread. Only the driver work can run elsewhere.
    private readonly List<MountSession> _sessions = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private bool _multiExiting;
    private bool HasMounted => _sessions.Any(s => s.Mounted);

    private static string PointKey(string point)
    {
        var p = point.Trim().TrimEnd('\\', '/');
        if (OperatingSystem.IsWindows() && p.Length >= 1 && char.IsAsciiLetter(p[0]) &&
            (p.Length == 1 || (p.Length == 2 && p[1] == ':')))
            return char.ToUpperInvariant(p[0]) + ":";
        return Path.GetFullPath(point).TrimEnd('\\', '/');
    }
    private static bool SamePoint(string? a, string? b) => a is not null && b is not null &&
        string.Equals(PointKey(a), PointKey(b), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // The existing CLI parser is reused for the initial and every forwarded mount.
    private async void ReceiveInstanceRequest(string[] args)
    {
        if (_multiExiting) return;
        try
        {
            if (args.Length == 0 || args.Contains("--show", StringComparer.OrdinalIgnoreCase))
                RestoreFromTray();
            var mountArgs = args.Where(a => !a.Equals("--show", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (mountArgs.Length > 0) await ProcessCommandLineArgsAsync(mountArgs);
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Mount request failed: {ex.Message}");
            if (!IsVisible) RestoreFromTray();
        }
    }

    private async Task MountWithProgressAsync(string archivePath, string? mountPoint = null)
    {
        if (_multiExiting) return;
        archivePath = Path.GetFullPath(archivePath);
        if (mountPoint is not null && OperatingSystem.IsWindows()) mountPoint = PointKey(mountPoint);
        if (mountPoint is not null && _sessions.Any(s => SamePoint(mountPoint, s.Point) ||
                SamePoint(mountPoint, s.RequestedPoint)))
        {
            _loggingService.LogError($"Mount point already reserved: {mountPoint}");
            if (!IsVisible) RestoreFromTray();
            return;
        }
        var session = new MountSession(new MountService(_loggingService, _settingsService), archivePath, mountPoint);
        _sessions.Add(session);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                session.Progress = new MountProgressWindow(Path.GetFileName(archivePath), mountPoint);
                session.Progress.Begin();
            }
            catch (Exception ex) { _loggingService.LogError($"Mount progress: {ex.Message}"); }
        }
        RefreshMountList();
        session.Lifetime = RunMountSessionAsync(session);
        // Return at driver readiness, NOT after unmount. The lifetime task is retained.
        await session.Ready.Task;
    }

    private void FinishProgress(MountSession session)
    {
        try { session.Progress?.Finish(); }
        catch (Exception ex) { _loggingService.LogError($"Mount progress: {ex.Message}"); }
        session.Progress = null;
    }

    private async Task RunMountSessionAsync(MountSession session)
    {
        await Task.Yield();
        var gateHeld = false;
        void OnStatus(object? sender, MountStatusChangedEventArgs e)
        {
            if (e.IsMounted && e.MountPoint is { } point)
                Dispatcher.UIThread.Post(() => SessionMounted(session, point), DispatcherPriority.Normal);
        }
        session.Service.MountStatusChanged += OnStatus;
        try
        {
            // Serialize mount INITIALIZATION so two auto-letter requests cannot race.
            // Already mounted filesystems continue independently.
            await _startGate.WaitAsync();
            gateHeld = true;
            if (_multiExiting) return;
            if (session.RequestedPoint is { } requested && _sessions.Any(s =>
                !ReferenceEquals(s, session) && SamePoint(requested, s.Point)))
                throw new InvalidOperationException($"Mount point already in use: {requested}");
#pragma warning disable IL3002
            // Each original backend understands a bare drive letter. Preserve folder paths.
            var backendPoint = session.RequestedPoint;
            if (OperatingSystem.IsWindows() && backendPoint is { Length: 2 } && backendPoint[1] == ':')
                backendPoint = backendPoint[..1];
            var backendLifetime = session.Service.MountAsync(session.Archive, backendPoint);
#pragma warning restore IL3002
            await Task.WhenAny(backendLifetime, session.Ready.Task);
            if (session.Service.IsMounted && session.Service.CurrentMountPoint is { } point)
                SessionMounted(session, point);
            if (session.Ready.Task.IsCompletedSuccessfully && session.Ready.Task.Result)
            {
                _startGate.Release();
                gateHeld = false;
            }
            await backendLifetime;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Mount failed ({session.Archive}): {ex.Message}");
        }
        finally
        {
            if (gateHeld) _startGate.Release();
            var succeeded = session.Ready.Task.IsCompletedSuccessfully && session.Ready.Task.Result;
            session.Ready.TrySetResult(false);
            FinishProgress(session);
            session.Ipc.Dispose();
            session.Service.MountStatusChanged -= OnStatus;
            // The backend's lifecycle can finish before its unmount cleanup task.
            // Wait for both before disposing the service and releasing the mount point.
            var stoppedSuccessfully = false;
            try
            {
                if (session.StopTask is not null) await session.StopTask;
                else if (session.Service.IsMounted) await session.Service.UnmountAsync();
                stoppedSuccessfully = succeeded && !session.Service.IsMounted;
            }
            catch (Exception ex) { _loggingService.LogError($"Unmount cleanup: {ex.Message}"); }
            try { await Task.Run(session.Service.Dispose); }
            catch (Exception ex)
            {
                stoppedSuccessfully = false;
                _loggingService.LogError($"Mount disposal: {ex.Message}");
            }
            _sessions.Remove(session);
            RefreshMountList();
            if (!succeeded && !_multiExiting && !IsVisible) RestoreFromTray();
            // Exit only after the final successful mount has been cleaned up.
            // Count queued/initializing sessions too, not just mounted drives.
            if (OperatingSystem.IsWindows() && stoppedSuccessfully && !_multiExiting && _sessions.Count == 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_multiExiting && _sessions.Count == 0) RequestMultiExit();
                }, DispatcherPriority.Background);
            }
        }
    }

    private void SessionMounted(MountSession session, string point)
    {
        if (!_sessions.Contains(session) || session.Ready.Task.IsCompleted) return;
        session.Point = point;
        _hadSuccessfulMount = true;
        FinishProgress(session);
        if (OperatingSystem.IsWindows())
        {
            session.Ipc.Start(point, async () =>
            {
                await Dispatcher.UIThread.InvokeAsync(async () => await StopSessionAsync(session));
                return !session.Service.IsMounted;
            });
        }
        session.Ready.TrySetResult(true);
        RefreshMountList();
        if (_settingsService.Settings.AutoOpenMountedDrive && !_multiExiting && !ShellHelper.OpenFolder(point))
            _loggingService.LogError($"Failed to open the mounted location: {point}");
    }

    private async Task StopSessionAsync(MountSession session)
    {
        if (!_sessions.Contains(session)) return;
        if (!await session.Ready.Task) return;
        if (session.StopTask is null)
        {
            session.Stopping = true;
            RefreshMountList();
            session.StopTask = session.Service.UnmountAsync();
        }
        try
        {
            await session.StopTask;
            await session.Lifetime;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Unmount failed ({session.Label}): {ex.Message}");
            if (!_multiExiting && !IsVisible) RestoreFromTray();
        }
    }

    private async Task ShutdownAllMountsAsync()
    {
        _multiExiting = true;
        _guiInstanceIpc?.Dispose();
        var sessions = _sessions.ToArray();
        await Task.WhenAll(sessions.Select(StopSessionAsync));
        await Task.WhenAll(sessions.Select(s => s.Lifetime));
    }

    private void DisposeMountSessions()
    {
        _multiExiting = true;
        foreach (var s in _sessions.ToArray())
        {
            FinishProgress(s);
            s.Ipc.Dispose();
            // Normal Exit drains all sessions first; fallback for OS shutdown.
            s.Service.Dispose();
        }
    }

    private void RefreshMountList()
    {
        var mounted = _sessions.Where(s => s.Mounted).ToArray();
        MountButton.IsEnabled = !_multiExiting;
        UnmountButton.IsEnabled = mounted.Length > 0 && !_multiExiting;
        MountStatusText.Text = string.Join(" | ", mounted.Select(s => s.Point));
        StatusText.Text = $"{mounted.Length} mounted, {_sessions.Count(s => !s.Ready.Task.IsCompleted)} mounting";
        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = $"SimpleZipDrive ({mounted.Length} mounted)";
            _trayIcon.Menu = BuildMountTrayMenu();
        }
    }

    private NativeMenu BuildMountTrayMenu()
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open SimpleZipDrive");
        open.Click += (_, _) => RestoreFromTray();
        menu.Add(open);
        foreach (var session in _sessions.Where(s => s.Mounted).ToArray())
        {
            var unmount = new NativeMenuItem($"Unmount {session.Label}");
            unmount.Click += async (_, _) => await StopSessionAsync(session);
            menu.Add(unmount);
        }
        var exit = new NativeMenuItem("Exit (unmount all)");
        exit.Click += (_, _) => RequestMultiExit();
        menu.Add(exit);
        return menu;
    }

    private void RequestMultiExit()
    {
        _allowExit = true;
        if (App.TryBeginShutdown()) _ = PerformShutdownAsync();
    }

    private async Task SelectAndUnmountAsync()
    {
        var mounted = _sessions.Where(s => s.Mounted).ToArray();
        if (mounted.Length == 0) return;
        if (mounted.Length == 1) { await StopSessionAsync(mounted[0]); return; }
        var list = new ListBox { ItemsSource = mounted.Select(s => s.Label).ToArray(), SelectedIndex = 0,
            MinHeight = 100, MaxHeight = 300 };
        var dialog = new Window { Title = "Unmount drive", Width = 480, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var ok = new Button { Content = "Unmount" };
        var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, _) => dialog.Close(list.SelectedIndex >= 0 ? mounted[list.SelectedIndex] : null);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel { Margin = new Thickness(14), Spacing = 10, Children = {
            new TextBlock { Text = "Select the drive to unmount." }, list,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } }
        } };
        var chosen = await dialog.ShowDialog<MountSession?>(this);
        if (chosen is not null) await StopSessionAsync(chosen);
    }
}

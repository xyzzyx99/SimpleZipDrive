using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SimpleZipDrive.Views;

/// <summary>
/// A brief, non-activating Windows notification shown only while mounting.
/// Active notifications from separate SimpleZipDrive processes are arranged
/// above the taskbar by enumerating their uniquely-prefixed top-level windows.
/// This is activity indication, NOT decompression percentage progress.
/// </summary>
internal sealed class MountProgressWindow : Window
{
    private const string WindowPrefix = "SimpleZipDrive Mount Progress ";
    private const double NotificationWidth = 348;
    private const double NotificationHeight = 106;
    private const double MarginDip = 14;
    private const double GapDip = 9;

    private readonly DispatcherTimer _showTimer;
    private readonly DispatcherTimer _stackTimer;
    private bool _finished;
    private bool _shown;

    public MountProgressWindow(string archiveName, string? mountPoint)
    {
        Title = WindowPrefix + Environment.ProcessId.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + ":" +
                Guid.NewGuid().ToString("N")[..8];
        Width = NotificationWidth;
        Height = NotificationHeight;
        CanResize = false;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;

        var title = new TextBlock
        {
            Text = "Mounting archive...",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        };
        var file = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(mountPoint)
                ? archiveName
                : $"{archiveName}   →   {mountPoint}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#CCD3DD")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = NotificationWidth - 32
        };
        var bar = new ProgressBar
        {
            IsIndeterminate = true,
            Minimum = 0,
            Maximum = 100,
            Height = 6,
            Background = new SolidColorBrush(Color.Parse("#3A4350")),
            Foreground = new SolidColorBrush(Color.Parse("#67B6FF"))
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#202936")),
            BorderBrush = new SolidColorBrush(Color.Parse("#4C596A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15, 11),
            Child = new StackPanel
            {
                Spacing = 7,
                Children = { title, file, bar }
            }
        };

        _showTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _showTimer.Tick += OnShowTimer;
        _stackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _stackTimer.Tick += OnStackTimer;
    }

    public void Begin()
    {
        if (!_finished)
            _showTimer.Start();
    }

    private void OnShowTimer(object? sender, EventArgs e)
    {
        _showTimer.Stop();
        if (_finished || !OperatingSystem.IsWindows()) return;
        _shown = true;
        SetInitialPosition();
        Show();
        RepositionInStack();
        _stackTimer.Start();
    }

    private void OnStackTimer(object? sender, EventArgs e) => RepositionInStack();

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _showTimer.Stop();
        _stackTimer.Stop();
        if (_shown)
            Close();
    }

    private void SetInitialPosition()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var area = screen.WorkingArea; // physical pixels, excluding taskbar
        var scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        Position = new PixelPoint(
            area.Right - (int)Math.Ceiling((NotificationWidth + MarginDip) * scale),
            area.Bottom - (int)Math.Ceiling((NotificationHeight + MarginDip) * scale));
    }

    private void RepositionInStack()
    {
        if (!_shown || !OperatingSystem.IsWindows()) return;
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        // EnumWindows/GetWindowText work across processes in the same desktop.
        // All instances use the same ordering, so their popups do not overlap.
        var windows = new List<(string Title, nint Handle)>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var caption = new StringBuilder(128);
            if (GetWindowText(handle, caption, caption.Capacity) > 0 &&
                caption.ToString().StartsWith(WindowPrefix, StringComparison.Ordinal))
                windows.Add((caption.ToString(), handle));
            return true;
        }, 0);
        windows.Sort((a, b) => string.CompareOrdinal(a.Title, b.Title));
        var slot = windows.FindIndex(w => string.Equals(w.Title, Title, StringComparison.Ordinal));
        if (slot < 0) slot = 0;

        var area = screen.WorkingArea;
        var scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var height = (int)Math.Ceiling(NotificationHeight * scale);
        var gap = (int)Math.Ceiling(GapDip * scale);
        var margin = (int)Math.Ceiling(MarginDip * scale);
        var x = area.Right - (int)Math.Ceiling(NotificationWidth * scale) - margin;
        var y = area.Bottom - height - margin - slot * (height + gap);
        Position = new PixelPoint(x, Math.Max(area.Y + margin, y));
    }

    private delegate bool EnumWindowsCallback(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);
}

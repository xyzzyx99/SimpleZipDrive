using Avalonia;

namespace SimpleZipDrive;

/// <summary>
///     Process entry point that bootstraps the Avalonia application.
/// </summary>
internal static class Program
{
    private static Mutex? _guiInstanceMutex;
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized yet.
    /// <summary>
    ///     Starts the Avalonia desktop application.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the application.</param>
    [STAThread]
    public static void Main(string[] args)
    {
        // --unmount is a client command: do not create a second GUI/application instance.
        if (OperatingSystem.IsWindows() && args.Length >= 1 &&
            args[0].Equals("--unmount", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: SimpleZipDrive.exe --unmount <drive-letter-or-mount-folder>");
                Environment.ExitCode = 2;
                return;
            }
            try
            {
                // Hard bound: a broken/stale server must never hang the CLI indefinitely.
                Environment.ExitCode = UnmountIpc.SendAsync(args[1])
                    .WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult() ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unmount failed: {ex.Message}");
                Environment.ExitCode = 1;
            }
            return;
        }
        // All Windows launches share the mount host (unmount clients handled above).
        if (OperatingSystem.IsWindows())
        {
            var instanceMutex = new Mutex(false, @"Local\" + GuiInstanceIpc.InstanceName,
                out var createdNew);
            if (!createdNew)
            {
                instanceMutex.Dispose();
                try
                {
                    Environment.ExitCode = GuiInstanceIpc.SignalAsync(args)
                        .GetAwaiter().GetResult() ? 0 : 1;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Unable to activate existing SimpleZipDrive: {ex.Message}");
                    Environment.ExitCode = 1;
                }
                return;
            }
            _guiInstanceMutex = instanceMutex;
        }
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
                global::SimpleZipDrive.Mounting.Dokan.DokanMountService.ReleaseSharedDokan();
            _guiInstanceMutex?.Dispose();
            _guiInstanceMutex = null;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    /// <summary>
    ///     Builds the Avalonia application configuration used by the desktop lifetime and the visual designer.
    /// </summary>
    /// <returns>The configured <see cref="AppBuilder" />.</returns>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}

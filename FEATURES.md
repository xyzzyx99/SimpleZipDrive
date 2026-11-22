# SimpleZipDrive – Enhanced Windows Features

This modified version of [SimpleZipDrive](https://github.com/purelogiccode/SimpleZipDrive) adds background mounting, command-line unmounting, persistent system tray operation, and support for multiple mounted archives within a single application instance. It also includes Windows x64 release and startup optimizations.

The new features work at the application level and retain the existing Dokan and WinFsp filesystem backends.

## 1. Command-Line Mounting

Mount archives directly from the command line without displaying the main application window. SimpleZipDrive runs in the system tray while the mounted drives remain accessible in Windows Explorer.

### Mount an archive to a specified drive

```cmd
SimpleZipDrive.exe "C:\Data\backup.zip" M
```

### Automatically select a drive letter

```cmd
SimpleZipDrive.exe "C:\Data\backup.zip"
```

### Mount an archive to a folder

```cmd
SimpleZipDrive.exe "C:\Data\backup.zip" "C:\Mount\Backup"
```

Folder mounting remains subject to the selected backend and its existing mount-point requirements.

### Mount with the main window visible

Use the optional `--show` switch to display the main window instead of mounting silently in tray mode.

```cmd
SimpleZipDrive.exe "C:\Data\backup.zip" M --show
```

### Open the main window

```cmd
SimpleZipDrive.exe
```

If the application is already running, this command restores the existing main window rather than starting another persistent instance.

## 2. Command-Line Unmounting

Unmount a specific drive without opening the main application window:

```cmd
SimpleZipDrive.exe --unmount M:
```

The drive letter without a colon is also accepted:

```cmd
SimpleZipDrive.exe --unmount M
```

For an archive mounted to a folder, specify its mount path:

```cmd
SimpleZipDrive.exe --unmount "C:\Mount\Backup"
```

The command sends an unmount request to the running application through local named-pipe communication. The application uses its existing unmount logic to release the selected drive and associated resources. Other mounted drives are unaffected.

A successful command acknowledgment means the request was accepted; filesystem cleanup may still be in progress. The command uses bounded timeouts to prevent indefinite waiting if the target mount is unavailable.

> Command-line unmounting is implemented independently of Windows Explorer's native **Eject** command. No modification to the Dokan or WinFsp drivers is required. A custom Explorer context-menu command could invoke this CLI in the future.

## 3. System Tray Integration

SimpleZipDrive provides a persistent notification-area (system tray) icon on Windows.

| Action | Behavior |
| --- | --- |
| Mount from the command line | Mount in the background without showing the main window. |
| Click the tray icon | Restore the main window. |
| Minimize the main window | Hide the window and its taskbar button; keep running in the tray. |
| Unmount one drive | Remove only the selected drive. |
| Unmount the last drive | Keep SimpleZipDrive running in the tray. |
| Close the main window (X) | Gracefully unmount all drives and exit. |
| Select **Exit (unmount all)** from the tray menu | Gracefully unmount all drives and exit. |

### Tray menu

The context menu provides an **Open SimpleZipDrive** command, an individual **Unmount** command for each mounted archive, and an **Exit (unmount all)** command. Each unmount entry identifies its drive or mount point and archive filename. The tray tooltip displays the mounted-drive count.

The tray icon remains available even when there are no mounted drives. Keeping the application initialized also avoids starting a new application host for every subsequent mount.

Minimizing and closing intentionally have different meanings: minimizing hides the interface, while closing terminates the application after cleanup.

## 4. Multiple Drives in a Single Process

Multiple archives can be mounted within the same running application. Additional command-line launches forward requests to the existing host instead of creating a separate persistent process and tray icon for each drive.

For example:

```cmd
SimpleZipDrive.exe "C:\Data\archive1.zip" M
SimpleZipDrive.exe "C:\Data\archive2.zip" N
SimpleZipDrive.exe "C:\Data\archive3.zip" O
```

All three mounted drives are managed by one application host and one tray icon. Temporary command-line client processes may run while forwarding requests and then exit.

Unmount drives independently:

```cmd
SimpleZipDrive.exe --unmount N:
SimpleZipDrive.exe --unmount M:
SimpleZipDrive.exe --unmount O:
```

After the final unmount, the host remains available in the system tray. Close the main window or select **Exit (unmount all)** to terminate it.

The main window displays the current mount points and the number of mounted and mounting archives. When multiple drives are mounted, the GUI's **Unmount** button displays a drive-selection dialog.

### Mount management

- Each archive has its own mount session, filesystem service, unmount endpoint, and progress notification.
- Duplicate explicit mount points are rejected.
- Mount initialization is serialized to prevent conflicts when automatically allocating drive letters; mounted drives operate independently afterward.
- Dokan's process-wide initialization is shared across sessions rather than released when an individual drive is unmounted.
- Shutdown coordinates the cleanup of all remaining mount sessions.

## 5. Stacked Mount Progress Notifications

A small progress notification appears in the bottom-right corner of the Windows desktop when mounting takes longer than approximately 300 milliseconds.

Each notification displays the archive filename, requested mount point (or automatic selection), and an animated progress bar. If multiple mount requests are pending, their notifications stack vertically above the taskbar.

- Fast mounts complete without displaying a notification, avoiding unnecessary flashing.
- Notifications do not activate the main window or add a taskbar button.
- A notification disappears when its drive becomes available or the mount attempt fails.
- Progress is tracked separately for each mount session.

The progress bars are **indeterminate**: they indicate ongoing activity rather than a measured percentage, because the existing mount services do not provide percentage-completion information.

## 6. Performance and Windows x64 Release Optimizations

### Windows x64 release

The GitHub Actions release workflow is restricted to the `win-x64` target. Windows ARM64, Linux, and macOS release bundles are excluded from this modified release workflow. The existing build and test verification is retained.

### ReadyToRun compilation

The Windows x64 release packaging enables .NET ReadyToRun compilation:

```text
-p:PublishReadyToRun=true
```

ReadyToRun precompiles portions of the managed application code, reducing some just-in-time compilation work during startup. It may increase package size and publishing time. It is a startup optimization, not a guarantee of faster decompression or filesystem I/O.

### Reduced application overhead

- Log entries are retained, but expensive log textbox updates are deferred while the main window is hidden and refreshed when it is restored.
- Automatic opening of mounted drives in Explorer is performed once per successful mount session when enabled in Settings.
- Single-process operation avoids maintaining a separate .NET runtime, Avalonia interface, and application host for every archive.
- Keeping the host alive in the system tray allows later mount requests to reuse initialized application services.

Performance improvements depend on the machine, archive format, size, backend, and workload. No quantitative speedup is claimed without benchmarking.

## 7. Command Reference

| Command | Description |
| --- | --- |
| `SimpleZipDrive.exe` | Open or restore the main window. |
| `SimpleZipDrive.exe "file.zip"` | Mount using an automatically selected drive letter. |
| `SimpleZipDrive.exe "file.zip" M` | Mount to drive M: in the background. |
| `SimpleZipDrive.exe "file.zip" M --show` | Mount to M: and display the main window. |
| `SimpleZipDrive.exe "file.zip" "C:\Mount\Zip"` | Mount to a folder in the background. |
| `SimpleZipDrive.exe --unmount M:` | Request unmounting of drive M:. |
| `SimpleZipDrive.exe --unmount "C:\Mount\Zip"` | Request unmounting of a folder mount. |

Quote archive paths and folder paths containing spaces. Specify a mount point when using `--unmount` so that the intended drive can be identified when multiple archives are mounted.

## 8. Compatibility and Implementation

The enhanced tray, IPC, and release features described here target Windows. The application retains its existing archive format support and Dokan/WinFsp backend selection. Install the selected filesystem backend as required by the original application.

Key implementation files:

| File | Responsibility |
| --- | --- |
| `SimpleZipDrive/Program.cs` | Command-line dispatch and single-instance host selection. |
| `SimpleZipDrive/App.axaml.cs` | Hidden startup and explicit application lifetime. |
| `SimpleZipDrive/GuiInstanceIpc.cs` | Forwarding launch and mount requests to the existing host. |
| `SimpleZipDrive/UnmountIpc.cs` | Mount-specific unmount request communication. |
| `SimpleZipDrive/Views/MainWindow.axaml.cs` | Main window, tray integration, and minimize/close behavior. |
| `SimpleZipDrive/Views/MainWindow.MultiMount.cs` | Multiple mount sessions, mount selection, tray menu, and cleanup. |
| `SimpleZipDrive/Views/MountProgressWindow.cs` | Delayed and stacked mounting progress notifications. |
| `SimpleZipDrive/Mounting/Dokan/DokanMountService.cs` | Shared Dokan driver initialization. |
| `.github/workflows/release.yml` | Windows x64-only release workflow. |
| `scripts/package-release.ps1` | Windows x64 ReadyToRun publishing. |

These enhancements are specific to the modified [SimpleZipDrive fork](https://github.com/xyzzyx99/SimpleZipDrive) and are not necessarily present in the upstream release.

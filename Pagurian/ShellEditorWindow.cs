using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using WinRT.Interop;

namespace Pagurian;

internal sealed record ShellEditorGeometry(
    TaskbarInterop.DisplayMonitor EditorMonitor,
    IReadOnlyList<TaskbarInterop.DisplayMonitor> Monitors,
    TaskbarTrayPlacement.Surface? TaskbarSurface);

// Owns one activated editor window on the display containing Shell_TrayWnd
// plus one non-activating blocker per other display. Each window paints an
// opaque, blurred snapshot captured immediately before the session opens.
static class ShellEditorWindow
{
    private sealed record InitialSnapshotBatch(
        IReadOnlyList<string> DeviceNames,
        IReadOnlyDictionary<string, ShellEditorSnapshot> Snapshots);

    private static readonly WindowKey MainKey = WindowKey.Of("pagurian-shell-editor");
    private static readonly Dictionary<string, ReactorWindow> BackdropWindows =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ShellEditorSnapshot> Snapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CapturedMonitorDevices =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SnapshotCaptures =
        new(StringComparer.OrdinalIgnoreCase);

    private static ReactorWindow? _window;
    private static DispatcherQueueTimer? _geometryTimer;
    private static CancellationTokenSource? _sessionCancellation;
    private static int _sessionGeneration;
    private static bool _opening;
    private static bool _allowClose;
    private static bool _restoreSettingsOnClose;
    private static bool _endingSession;
    private static bool _refreshingGeometry;
    private static bool _topologyFailureReported;
    private static ShellEditorGeometry _geometry = new(
        default,
        Array.Empty<TaskbarInterop.DisplayMonitor>(),
        null);

    internal static ShellEditorGeometry Geometry => _geometry;
    internal static bool AllowClose => _allowClose;
    internal static event Action? GeometryChanged;
    internal static event Action? SnapshotChanged;

    internal static ShellEditorSnapshot? SnapshotFor(string? deviceName) =>
        !string.IsNullOrWhiteSpace(deviceName) &&
        Snapshots.TryGetValue(deviceName, out var snapshot)
            ? snapshot
            : null;

    public static void OpenOrActivate()
    {
        var existing = _window ?? ReactorApp.FindWindow(MainKey);
        if (existing != null)
        {
            existing.Activate();
            return;
        }
        if (_opening)
            return;

        if (!TryReadGeometry(out _geometry))
        {
            TaskbarInterop.ShowMessage(
                "Pagurian could not read the current display layout.",
                "Edit Shells");
            return;
        }

        try
        {
            _allowClose = false;
            _topologyFailureReported = false;
            _opening = true;
            _restoreSettingsOnClose = SettingsWindow.HideForShellEditor();
            TaskbarController.SetShellEditorActive(true);

            _sessionCancellation?.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            var generation = ++_sessionGeneration;
            if (!TaskbarInterop.FlushDesktopComposition())
                PagurianLog.HostError("shell editor: DwmFlush failed before desktop capture");

            BeginInitialCapture(generation, _geometry, _sessionCancellation.Token);
        }
        catch (Exception ex)
        {
            PagurianLog.HostError("shell editor: failed to start desktop capture", ex);
            FinishSession(_window, restoreSettings: true);
            TaskbarInterop.ShowMessage(
                $"Pagurian could not open the Shell editor.\n\n{ex.Message}",
                "Edit Shells");
        }
    }

    internal static void RequestClose() => _window?.Close();

    // Used after Save or an explicit discard confirmation. Queueing avoids
    // closing native windows from inside ContentDialog teardown.
    internal static void CloseWithoutPrompt()
    {
        _allowClose = true;
        ReactorApp.UIDispatcher?.TryEnqueue(() =>
        {
            if (_opening || _window != null || BackdropWindows.Count > 0)
                FinishSession(_window, restoreSettings: true);
        });
    }

    // Process shutdown is already an explicit destructive action, so it must
    // not be blocked by a draft or restore a hidden Settings window.
    public static void CloseIfOpen()
    {
        _allowClose = true;
        _restoreSettingsOnClose = false;
        FinishSession(_window, restoreSettings: false);
    }

    private static void BeginInitialCapture(
        int generation,
        ShellEditorGeometry geometry,
        CancellationToken cancellationToken)
    {
        _ = CaptureInitialAsync(generation, geometry, cancellationToken);
    }

    private static async Task CaptureInitialAsync(
        int generation,
        ShellEditorGeometry geometry,
        CancellationToken cancellationToken)
    {
        try
        {
            var batch = await Task.Run(
                    () => CaptureInitialBatch(geometry.Monitors, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            await RunOnUiAsync(() => CompleteInitialCapture(
                    generation,
                    batch,
                    cancellationToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session shutdown invalidates this opening attempt.
        }
        catch (Exception ex)
        {
            try
            {
                await RunOnUiAsync(() => FailOpening(generation, ex))
                    .ConfigureAwait(false);
            }
            catch
            {
                // The UI dispatcher can disappear during process shutdown.
            }
        }
    }

    private static InitialSnapshotBatch CaptureInitialBatch(
        IReadOnlyList<TaskbarInterop.DisplayMonitor> monitors,
        CancellationToken cancellationToken)
    {
        var snapshots = new Dictionary<string, ShellEditorSnapshot>(
            StringComparer.OrdinalIgnoreCase);
        var devices = new List<string>(monitors.Count);

        foreach (var monitor in monitors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            devices.Add(monitor.DeviceName);
            try
            {
                var frame = ShellEditorSnapshotService.Capture(monitor);
                if (frame == null)
                {
                    PagurianLog.HostError(
                        $"shell editor: desktop capture failed for {monitor.DeviceName}; using solid fallback");
                    continue;
                }

                snapshots[monitor.DeviceName] = ShellEditorSnapshotService.Blur(
                    monitor.DeviceName,
                    frame.Value,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PagurianLog.HostError(
                    $"shell editor: desktop snapshot failed for {monitor.DeviceName}; using solid fallback",
                    ex);
            }
        }

        return new InitialSnapshotBatch(devices, snapshots);
    }

    private static void CompleteInitialCapture(
        int generation,
        InitialSnapshotBatch batch,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentSession(generation) || cancellationToken.IsCancellationRequested)
            return;

        if (!TryReadGeometry(out var current))
        {
            FailOpening(
                generation,
                new InvalidOperationException("Could not re-read the display layout after capture."));
            return;
        }

        // If a device was added or removed while the initial snapshots were
        // being prepared, restart before showing any window. Rect/DPI changes
        // for the same devices reuse and stretch the just-captured frame.
        if (!SameDeviceSet(batch.DeviceNames, current.Monitors))
        {
            _geometry = current;
            BeginInitialCapture(generation, current, cancellationToken);
            return;
        }

        _geometry = current;
        Snapshots.Clear();
        foreach (var (deviceName, snapshot) in batch.Snapshots)
            Snapshots[deviceName] = snapshot;
        CapturedMonitorDevices.Clear();
        foreach (var monitor in current.Monitors)
            CapturedMonitorDevices.Add(monitor.DeviceName);

        try
        {
            OpenWindows();
            _opening = false;
        }
        catch (Exception ex)
        {
            FailOpening(generation, ex);
        }
    }

    private static void OpenWindows()
    {
        var window = ReactorApp.OpenWindow(
            CreateSpec(MainKey, "Pagurian Shell Editor", noActivate: false),
            () => new ShellEditorView());
        _window = window;
        window.Closed += (_, _) => OnMainWindowClosed(window);
        window.DpiChanged += (_, _) =>
        {
            GeometryChanged?.Invoke();
            RefreshGeometry();
        };

        foreach (var monitor in SecondaryMonitors(_geometry))
            CreateBackdropWindow(monitor, show: false);

        ApplyMonitorBounds(window, _geometry.EditorMonitor.MonitorRect);
        StartGeometryTimer();

        foreach (var backdrop in BackdropWindows.Values)
            backdrop.Show();
        window.Show();
        window.Activate();

        var rect = _geometry.EditorMonitor.MonitorRect;
        PagurianLog.Host(
            $"shell editor: opened {_geometry.Monitors.Count} static snapshot surfaces; " +
            $"interactive={_geometry.EditorMonitor.DeviceName} " +
            $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
    }

    private static void FailOpening(int generation, Exception ex)
    {
        if (!IsCurrentSession(generation))
            return;

        PagurianLog.HostError("shell editor: failed to open", ex);
        FinishSession(_window, restoreSettings: true);
        TaskbarInterop.ShowMessage(
            $"Pagurian could not open the Shell editor.\n\n{ex.Message}",
            "Edit Shells");
    }

    private static WindowSpec CreateSpec(
        WindowKey key,
        string title,
        bool noActivate) => new()
    {
        Title = title,
        Width = 1,
        Height = 1,
        Style = WindowStyle.None,
        CornerStyle = WindowCornerStyle.Square,
        Backdrop = BackdropChoice.Of(BackdropKind.None),
        ShowInTaskbar = false,
        ShowInSwitcher = false,
        NoActivate = noActivate,
        ActivateOnOpen = false,
        IsMinimizable = false,
        IsMaximizable = false,
        IsMovableByBackground = false,
        ResizeMode = WindowResizeMode.NoResize,
        Level = WindowLevel.AlwaysOnTop,
        StartPosition = WindowStartPosition.Manual,
        ManualPosition = (0, 0),
        Key = key,
        Icon = WindowIcon.FromPath(AppAssets.IconPath),
    };

    private static IEnumerable<TaskbarInterop.DisplayMonitor> SecondaryMonitors(
        ShellEditorGeometry geometry) =>
        geometry.Monitors.Where(monitor => !SameMonitor(
            monitor,
            geometry.EditorMonitor));

    private static void CreateBackdropWindow(
        TaskbarInterop.DisplayMonitor monitor,
        bool show)
    {
        ReactorWindow? window = null;
        try
        {
            window = ReactorApp.OpenWindow(
                CreateSpec(
                    WindowKey.Of($"pagurian-shell-editor-backdrop:{monitor.DeviceName}"),
                    "Pagurian Shell Editor Backdrop",
                    noActivate: true),
                () => new ShellEditorBackdropView(monitor.DeviceName));
            window.Closed += (_, _) => OnBackdropWindowClosed(monitor.DeviceName, window);
            ApplyMonitorBounds(window, monitor.MonitorRect);
            BackdropWindows.Add(monitor.DeviceName, window);
            if (show)
                window.Show();
        }
        catch
        {
            if (window != null)
            {
                try { window.Close(); }
                catch { /* preserve the original creation failure */ }
            }
            throw;
        }
    }

    private static void StartGeometryTimer()
    {
        _geometryTimer?.Stop();
        var dispatcher = ReactorApp.UIDispatcher;
        if (dispatcher == null)
            return;

        _geometryTimer = dispatcher.CreateTimer();
        _geometryTimer.Interval = TimeSpan.FromMilliseconds(250);
        _geometryTimer.IsRepeating = true;
        _geometryTimer.Tick += (_, _) => RefreshGeometry();
        _geometryTimer.Start();
    }

    private static void RefreshGeometry()
    {
        var mainWindow = _window;
        if (mainWindow == null || _endingSession || _refreshingGeometry)
            return;

        _refreshingGeometry = true;
        try
        {
            if (!TryReadGeometry(out var next))
                throw new InvalidOperationException(
                    "Could not enumerate the updated display layout.");

            var waitingForCapture = false;
            foreach (var monitor in next.Monitors)
            {
                if (CapturedMonitorDevices.Contains(monitor.DeviceName))
                    continue;

                waitingForCapture = true;
                BeginAddedMonitorCapture(monitor);
            }
            if (waitingForCapture)
                return;

            ReconcileGeometry(mainWindow, next);
            _topologyFailureReported = false;
        }
        catch (Exception ex)
        {
            PagurianLog.HostError("shell editor: failed to reconcile display topology", ex);
            if (!_topologyFailureReported)
            {
                _topologyFailureReported = true;
                TaskbarInterop.ShowMessage(
                    "Pagurian could not cover the updated display layout. " +
                    "The Shell editor will close; unsaved changes still require confirmation.",
                    "Edit Shells");
                _window?.Close();
            }
        }
        finally
        {
            _refreshingGeometry = false;
        }
    }

    private static void BeginAddedMonitorCapture(
        TaskbarInterop.DisplayMonitor monitor)
    {
        if (!SnapshotCaptures.Add(monitor.DeviceName) ||
            _sessionCancellation == null)
        {
            return;
        }

        var generation = _sessionGeneration;
        _ = CaptureAddedMonitorAsync(
            generation,
            monitor,
            _sessionCancellation.Token);
    }

    private static async Task CaptureAddedMonitorAsync(
        int generation,
        TaskbarInterop.DisplayMonitor monitor,
        CancellationToken cancellationToken)
    {
        ShellEditorCapturedFrame? frame = null;
        Exception? captureError = null;
        try
        {
            frame = await Task.Run(
                    () => ShellEditorSnapshotService.Capture(monitor),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            captureError = ex;
        }

        try
        {
            await RunOnUiAsync(() => CompleteAddedMonitorCapture(
                    generation,
                    monitor,
                    frame,
                    captureError,
                    cancellationToken))
                .ConfigureAwait(false);
        }
        catch
        {
            // The dispatcher can disappear during process shutdown.
        }
    }

    private static void CompleteAddedMonitorCapture(
        int generation,
        TaskbarInterop.DisplayMonitor capturedMonitor,
        ShellEditorCapturedFrame? frame,
        Exception? captureError,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentSession(generation) || cancellationToken.IsCancellationRequested)
            return;

        SnapshotCaptures.Remove(capturedMonitor.DeviceName);
        if (!TryReadGeometry(out var current))
        {
            RefreshGeometry();
            return;
        }

        var actualMonitor = current.Monitors.FirstOrDefault(monitor =>
            string.Equals(
                monitor.DeviceName,
                capturedMonitor.DeviceName,
                StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(actualMonitor.DeviceName))
            return;

        if (!actualMonitor.MonitorRect.Equals(capturedMonitor.MonitorRect))
        {
            RefreshGeometry();
            return;
        }

        CapturedMonitorDevices.Add(capturedMonitor.DeviceName);
        if (frame == null)
        {
            PagurianLog.HostError(
                $"shell editor: desktop capture failed for {capturedMonitor.DeviceName}; using solid fallback",
                captureError);
        }

        // Capturing happened before this display received a Pagurian window.
        // It can now be covered immediately by a solid fallback while the
        // captured pixels are filtered on the worker thread.
        RefreshGeometry();
        if (frame != null)
            _ = BlurAddedMonitorAsync(
                generation,
                capturedMonitor.DeviceName,
                frame.Value,
                cancellationToken);
    }

    private static async Task BlurAddedMonitorAsync(
        int generation,
        string deviceName,
        ShellEditorCapturedFrame frame,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await Task.Run(
                    () => ShellEditorSnapshotService.Blur(
                        deviceName,
                        frame,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            await RunOnUiAsync(() =>
            {
                if (!IsCurrentSession(generation) ||
                    cancellationToken.IsCancellationRequested ||
                    !CapturedMonitorDevices.Contains(deviceName) ||
                    !_geometry.Monitors.Any(monitor => string.Equals(
                        monitor.DeviceName,
                        deviceName,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                Snapshots[deviceName] = snapshot;
                SnapshotChanged?.Invoke();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session shutdown invalidates this background filter.
        }
        catch (Exception ex)
        {
            PagurianLog.HostError(
                $"shell editor: desktop blur failed for {deviceName}; keeping solid fallback",
                ex);
        }
    }

    private static void ReconcileGeometry(
        ReactorWindow mainWindow,
        ShellEditorGeometry next)
    {
        var desiredSecondary = SecondaryMonitors(next)
            .ToDictionary(
                monitor => monitor.DeviceName,
                StringComparer.OrdinalIgnoreCase);

        // Create the old-main-display blocker before moving the editor to a
        // newly selected main display, so no existing screen is exposed.
        foreach (var (deviceName, monitor) in desiredSecondary)
        {
            if (BackdropWindows.TryGetValue(deviceName, out var existing))
                ApplyMonitorBounds(existing, monitor.MonitorRect);
            else
                CreateBackdropWindow(monitor, show: true);
        }

        var geometryChanged = !SameGeometry(_geometry, next);
        _geometry = next;
        ApplyMonitorBounds(mainWindow, next.EditorMonitor.MonitorRect);

        foreach (var deviceName in BackdropWindows.Keys
                     .Where(deviceName => !desiredSecondary.ContainsKey(deviceName))
                     .ToArray())
        {
            var obsolete = BackdropWindows[deviceName];
            BackdropWindows.Remove(deviceName);
            try { obsolete.Close(); }
            catch { /* a removed display may already have destroyed it */ }
        }

        var activeDevices = next.Monitors
            .Select(monitor => monitor.DeviceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var deviceName in Snapshots.Keys
                     .Where(deviceName => !activeDevices.Contains(deviceName))
                     .ToArray())
        {
            Snapshots.Remove(deviceName);
        }
        CapturedMonitorDevices.RemoveWhere(deviceName =>
            !activeDevices.Contains(deviceName));

        if (geometryChanged)
            GeometryChanged?.Invoke();
    }

    private static bool TryReadGeometry(out ShellEditorGeometry geometry)
    {
        if (!TaskbarInterop.TryGetDisplayMonitors(out var monitors))
        {
            geometry = null!;
            return false;
        }

        TaskbarTrayPlacement.Surface? taskbarSurface =
            TaskbarTrayPlacement.TryGetSurface(out var surface) ? surface : null;

        TaskbarInterop.DisplayMonitor editorMonitor = default;
        TaskbarInterop.RECT taskbarRect;
        bool hasTaskbarRect;
        if (taskbarSurface is { } candidateSurface)
        {
            taskbarRect = candidateSurface.ParentRect;
            hasTaskbarRect = true;
        }
        else
        {
            hasTaskbarRect = TaskbarInterop.TryGetTaskbarRect(out taskbarRect);
        }

        if (hasTaskbarRect &&
            TaskbarInterop.TryGetDisplayMonitor(taskbarRect, out var taskbarMonitor))
        {
            editorMonitor = monitors.FirstOrDefault(monitor =>
                SameMonitor(monitor, taskbarMonitor));
        }

        if (string.IsNullOrWhiteSpace(editorMonitor.DeviceName))
            editorMonitor = monitors.FirstOrDefault(monitor => monitor.IsPrimary);
        if (string.IsNullOrWhiteSpace(editorMonitor.DeviceName))
            editorMonitor = monitors[0];

        if (taskbarSurface is { } candidate &&
            (!TaskbarInterop.TryGetDisplayMonitor(candidate.ParentRect, out var surfaceMonitor) ||
             !SameMonitor(surfaceMonitor, editorMonitor)))
        {
            taskbarSurface = null;
        }

        geometry = new ShellEditorGeometry(editorMonitor, monitors, taskbarSurface);
        return true;
    }

    private static bool SameDeviceSet(
        IReadOnlyList<string> capturedDevices,
        IReadOnlyList<TaskbarInterop.DisplayMonitor> currentMonitors)
    {
        if (capturedDevices.Count != currentMonitors.Count)
            return false;

        var devices = capturedDevices.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return currentMonitors.All(monitor => devices.Contains(monitor.DeviceName));
    }

    private static bool SameGeometry(
        ShellEditorGeometry left,
        ShellEditorGeometry right)
    {
        if (!left.EditorMonitor.Equals(right.EditorMonitor) ||
            left.TaskbarSurface != right.TaskbarSurface ||
            left.Monitors.Count != right.Monitors.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Monitors.Count; i++)
        {
            if (!left.Monitors[i].Equals(right.Monitors[i]))
                return false;
        }
        return true;
    }

    private static bool SameMonitor(
        TaskbarInterop.DisplayMonitor left,
        TaskbarInterop.DisplayMonitor right) =>
        string.Equals(
            left.DeviceName,
            right.DeviceName,
            StringComparison.OrdinalIgnoreCase);

    private static void ApplyMonitorBounds(
        ReactorWindow window,
        TaskbarInterop.RECT monitorRect)
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
        if (hwnd == IntPtr.Zero ||
            !TaskbarInterop.SetWindowPos(
                hwnd,
                TaskbarInterop.HWND_TOPMOST,
                monitorRect.Left,
                monitorRect.Top,
                monitorRect.Width,
                monitorRect.Height,
                TaskbarInterop.SWP_NOACTIVATE))
        {
            throw new InvalidOperationException(
                $"Could not position Shell editor surface for " +
                $"({monitorRect.Left},{monitorRect.Top})-" +
                $"({monitorRect.Right},{monitorRect.Bottom}).");
        }
    }

    private static void OnBackdropWindowClosed(
        string deviceName,
        ReactorWindow window)
    {
        if (_endingSession)
            return;

        if (BackdropWindows.TryGetValue(deviceName, out var current) &&
            ReferenceEquals(current, window))
        {
            BackdropWindows.Remove(deviceName);
        }
    }

    private static void OnMainWindowClosed(ReactorWindow window)
    {
        if (_endingSession || !ReferenceEquals(_window, window))
            return;

        _window = null;
        FinishSession(mainWindow: null, restoreSettings: true);
    }

    private static void FinishSession(
        ReactorWindow? mainWindow,
        bool restoreSettings)
    {
        if (_endingSession)
            return;

        _endingSession = true;
        try
        {
            ++_sessionGeneration;
            var cancellation = _sessionCancellation;
            _sessionCancellation = null;
            try { cancellation?.Cancel(); }
            catch { /* cancellation is best effort during shutdown */ }
            cancellation?.Dispose();

            _geometryTimer?.Stop();
            _geometryTimer = null;
            _window = null;
            _opening = false;
            _allowClose = true;
            SnapshotCaptures.Clear();
            CapturedMonitorDevices.Clear();
            Snapshots.Clear();

            var backdrops = BackdropWindows.Values.ToArray();
            BackdropWindows.Clear();
            foreach (var backdrop in backdrops)
            {
                try { backdrop.Close(); }
                catch { /* the monitor or native window may already be gone */ }
            }

            if (mainWindow != null)
            {
                try { mainWindow.Close(); }
                catch { /* the native window may already be gone */ }
            }

            TaskbarController.SetShellEditorActive(false);
            var shouldRestoreSettings = restoreSettings && _restoreSettingsOnClose;
            _restoreSettingsOnClose = false;
            SettingsWindow.RestoreAfterShellEditor(shouldRestoreSettings);
            PagurianLog.Host("shell editor: closed all static snapshot surfaces");
        }
        finally
        {
            _allowClose = false;
            _topologyFailureReported = false;
            _endingSession = false;
        }
    }

    private static bool IsCurrentSession(int generation) =>
        generation == _sessionGeneration && _sessionCancellation != null;

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = ReactorApp.UIDispatcher;
        if (dispatcher == null)
            return Task.FromException(
                new InvalidOperationException("The UI dispatcher is unavailable."));

        if (dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetException(
                new InvalidOperationException("Could not enqueue work on the UI dispatcher."));
        }

        return completion.Task;
    }
}

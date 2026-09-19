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
// plus one non-activating blocker per other display. The catalog and target
// remain in the same HWND, so typed drag payloads never cross windows.
static class ShellEditorWindow
{
    private static readonly WindowKey MainKey = WindowKey.Of("pagurian-shell-editor");
    private static readonly Dictionary<string, ReactorWindow> BackdropWindows =
        new(StringComparer.OrdinalIgnoreCase);

    private static ReactorWindow? _window;
    private static DispatcherQueueTimer? _geometryTimer;
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

    public static void OpenOrActivate()
    {
        var existing = _window ?? ReactorApp.FindWindow(MainKey);
        if (existing != null)
        {
            existing.Activate();
            return;
        }

        if (!TryReadGeometry(out _geometry))
        {
            TaskbarInterop.ShowMessage(
                "Pagurian could not read the current display layout.",
                "Edit Shells");
            return;
        }

        _allowClose = false;
        _topologyFailureReported = false;
        _restoreSettingsOnClose = SettingsWindow.HideForShellEditor();
        ReactorWindow? openedWindow = null;

        try
        {
            var window = ReactorApp.OpenWindow(
                CreateSpec(MainKey, "Pagurian Shell Editor", noActivate: false),
                () => new ShellEditorView());
            openedWindow = window;
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
            TaskbarController.SetShellEditorActive(true);
            StartGeometryTimer();

            foreach (var backdrop in BackdropWindows.Values)
                backdrop.Show();
            window.Show();
            window.Activate();

            var rect = _geometry.EditorMonitor.MonitorRect;
            PagurianLog.Host(
                $"shell editor: opened {_geometry.Monitors.Count} per-monitor surfaces; " +
                $"interactive={_geometry.EditorMonitor.DeviceName} " +
                $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
        }
        catch (Exception ex)
        {
            FinishSession(openedWindow, restoreSettings: true);
            PagurianLog.HostError("shell editor: failed to open", ex);
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
            if (_window != null || BackdropWindows.Count > 0)
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
        Backdrop = BackdropChoice.Of(ShellEditorBackdrop.DesktopAcrylicSupported
            ? BackdropKind.DesktopAcrylic
            : BackdropKind.None),
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
                () => new ShellEditorBackdropView());
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

            var desiredSecondary = SecondaryMonitors(next)
                .ToDictionary(
                    monitor => monitor.DeviceName,
                    StringComparer.OrdinalIgnoreCase);

            // Create the old-main-display blocker before moving the editor to
            // a newly selected main display, so no existing screen is exposed.
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

            if (geometryChanged)
                GeometryChanged?.Invoke();
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
            _geometryTimer?.Stop();
            _geometryTimer = null;
            _window = null;
            _allowClose = true;

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
            PagurianLog.Host("shell editor: closed all per-monitor surfaces");
        }
        finally
        {
            _allowClose = false;
            _topologyFailureReported = false;
            _endingSession = false;
        }
    }
}

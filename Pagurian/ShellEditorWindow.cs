using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using WinRT.Interop;

namespace Pagurian;

internal readonly record struct ShellEditorGeometry(
    TaskbarInterop.RECT VirtualScreenPx,
    TaskbarInterop.RECT PrimaryScreenPx,
    TaskbarTrayPlacement.Surface? TaskbarSurface);

// Owns the single activated window that covers the physical virtual desktop.
// The XAML content is one surface so typed drag payloads stay in-process and
// reliable between the centered catalog and taskbar-aligned target.
static class ShellEditorWindow
{
    private static readonly WindowKey Key = WindowKey.Of("pagurian-shell-editor");
    private static ReactorWindow? _window;
    private static DispatcherQueueTimer? _geometryTimer;
    private static bool _allowClose;
    private static bool _restoreSettingsOnClose;
    private static ShellEditorGeometry _geometry;

    internal static ShellEditorGeometry Geometry => _geometry;
    internal static bool AllowClose => _allowClose;
    internal static bool DesktopAcrylicSupported
    {
        get
        {
            try { return DesktopAcrylicController.IsSupported(); }
            catch { return false; }
        }
    }
    internal static event Action? GeometryChanged;

    public static void OpenOrActivate()
    {
        var existing = _window ?? ReactorApp.FindWindow(Key);
        if (existing != null)
        {
            existing.Activate();
            return;
        }

        if (!TryReadGeometry(out _geometry))
        {
            TaskbarInterop.ShowMessage(
                "Pagurian could not read the current desktop layout.",
                "Edit Shells");
            return;
        }

        _allowClose = false;
        _restoreSettingsOnClose = SettingsWindow.HideForShellEditor();
        ReactorWindow? openedWindow = null;

        try
        {
            var window = ReactorApp.OpenWindow(CreateSpec(), () => new ShellEditorView());
            openedWindow = window;
            _window = window;
            window.Closed += (_, _) => OnClosed(window);
            window.DpiChanged += (_, _) => RefreshGeometry();

            TaskbarController.SetShellEditorActive(true);
            ApplyVirtualScreenBounds(window, _geometry.VirtualScreenPx);
            StartGeometryTimer();
            window.Show();
            window.Activate();
            PagurianLog.Host(
                $"shell editor: opened across ({_geometry.VirtualScreenPx.Left},{_geometry.VirtualScreenPx.Top})-" +
                $"({_geometry.VirtualScreenPx.Right},{_geometry.VirtualScreenPx.Bottom})");
        }
        catch (Exception ex)
        {
            _geometryTimer?.Stop();
            _geometryTimer = null;
            _allowClose = true;
            try { openedWindow?.Close(); }
            catch { /* ignore cleanup failure; report the original error */ }
            _window = null;
            _allowClose = false;
            TaskbarController.SetShellEditorActive(false);
            SettingsWindow.RestoreAfterShellEditor(_restoreSettingsOnClose);
            _restoreSettingsOnClose = false;
            PagurianLog.HostError("shell editor: failed to open", ex);
            TaskbarInterop.ShowMessage(
                $"Pagurian could not open the Shell editor.\n\n{ex.Message}",
                "Edit Shells");
        }
    }

    internal static void RequestClose() => _window?.Close();

    // Used after Save or an explicit discard confirmation. Queueing avoids
    // closing a native window from inside ContentDialog teardown.
    internal static void CloseWithoutPrompt()
    {
        _allowClose = true;
        ReactorApp.UIDispatcher?.TryEnqueue(() => _window?.Close());
    }

    // Process shutdown is already an explicit destructive action, so it must
    // not be blocked by an editor draft or restore a hidden Settings window.
    public static void CloseIfOpen()
    {
        var existing = _window ?? ReactorApp.FindWindow(Key);
        _allowClose = true;
        _restoreSettingsOnClose = false;
        if (existing == null)
            return;

        try { existing.Close(); }
        catch { /* the native window may already be gone */ }
    }

    private static WindowSpec CreateSpec() => new()
    {
        Title = "Pagurian Shell Editor",
        Width = 1,
        Height = 1,
        Style = WindowStyle.None,
        CornerStyle = WindowCornerStyle.Square,
        Backdrop = BackdropChoice.Of(DesktopAcrylicSupported
            ? BackdropKind.DesktopAcrylic
            : BackdropKind.None),
        ShowInTaskbar = false,
        ShowInSwitcher = false,
        NoActivate = false,
        ActivateOnOpen = false,
        IsMinimizable = false,
        IsMaximizable = false,
        IsMovableByBackground = false,
        ResizeMode = WindowResizeMode.NoResize,
        Level = WindowLevel.AlwaysOnTop,
        StartPosition = WindowStartPosition.Manual,
        ManualPosition = (0, 0),
        Key = Key,
        Icon = WindowIcon.FromPath(AppAssets.IconPath),
    };

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
        var window = _window;
        if (window == null || !TryReadGeometry(out var next))
            return;

        if (!next.VirtualScreenPx.Equals(_geometry.VirtualScreenPx))
            ApplyVirtualScreenBounds(window, next.VirtualScreenPx);

        if (next == _geometry)
            return;

        _geometry = next;
        GeometryChanged?.Invoke();
    }

    private static bool TryReadGeometry(out ShellEditorGeometry geometry)
    {
        if (!TaskbarInterop.TryGetDesktopRects(out var virtualScreen, out var primaryScreen))
        {
            geometry = default;
            return false;
        }

        TaskbarTrayPlacement.Surface? taskbar =
            TaskbarTrayPlacement.TryGetSurface(out var surface) ? surface : null;
        geometry = new ShellEditorGeometry(virtualScreen, primaryScreen, taskbar);
        return true;
    }

    private static void ApplyVirtualScreenBounds(
        ReactorWindow window,
        TaskbarInterop.RECT virtualScreen)
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
        TaskbarInterop.SetWindowPos(
            hwnd,
            TaskbarInterop.HWND_TOPMOST,
            virtualScreen.Left,
            virtualScreen.Top,
            virtualScreen.Width,
            virtualScreen.Height,
            TaskbarInterop.SWP_NOACTIVATE);
    }

    private static void OnClosed(ReactorWindow window)
    {
        if (!ReferenceEquals(_window, window))
            return;

        _geometryTimer?.Stop();
        _geometryTimer = null;
        _window = null;
        _allowClose = false;
        TaskbarController.SetShellEditorActive(false);

        var restoreSettings = _restoreSettingsOnClose;
        _restoreSettingsOnClose = false;
        SettingsWindow.RestoreAfterShellEditor(restoreSettings);
        PagurianLog.Host("shell editor: closed");
    }
}

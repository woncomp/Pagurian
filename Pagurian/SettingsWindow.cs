using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace Pagurian;

// Owns the singleton settings window: opened from the tray icon's
// double-click or its context menu, re-activated when already open, and
// closed as part of the quit sequence so the view never observes torn-down
// module state.
static class SettingsWindow
{
    private static readonly WindowKey Key = WindowKey.Of("pagurian-settings");
    private static ReactorWindow? _window;

    // For HWND-needing dialogs (the folder picker) owned by this window.
    internal static Microsoft.UI.WindowId? AppWindowId => _window?.AppWindow.Id;

    public static void OpenOrActivate()
    {
        var existing = _window ?? ReactorApp.FindWindow(Key);
        if (existing != null)
        {
            existing.Activate();
            return;
        }

        var window = ReactorApp.OpenWindow(CreateSpec(), () => new SettingsView());
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
                _window = null;
        };
        _window = window;
        window.Show();
        PagurianLog.Host("settings: window opened");
    }

    public static void CloseIfOpen()
    {
        var existing = _window ?? ReactorApp.FindWindow(Key);
        _window = null;
        if (existing == null)
            return;
        try { existing.Close(); }
        catch { /* the native window may already be gone */ }
    }

    private static WindowSpec CreateSpec() => new()
    {
        Title = "Pagurian Settings",
        Width = 780,
        Height = 600,
        Style = WindowStyle.Default,
        Backdrop = BackdropChoice.Of(BackdropKind.AcrylicThin),
        ShowInTaskbar = true,
        ShowInSwitcher = true,
        NoActivate = false,
        IsMinimizable = true,
        IsMaximizable = false,
        ResizeMode = WindowResizeMode.CanResize,
        MinWidth = 620,
        MinHeight = 460,
        Level = WindowLevel.Normal,
        StartPosition = WindowStartPosition.CenterOnPrimary,
        Key = Key,
        Icon = WindowIcon.FromPath(AppAssets.IconPath),
    };
}

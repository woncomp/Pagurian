using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace Pagurian;

internal enum SettingsPage
{
    Shells,
    General,
}

// Owns the singleton paged settings window. Callers choose the page to show;
// repeated requests navigate and activate the existing window.
static class SettingsWindow
{
    private static readonly WindowKey Key = WindowKey.Of("pagurian-settings");
    private static ReactorWindow? _window;
    private static SettingsPage _requestedPage = SettingsPage.General;
    private static bool _allowClose;

    internal static Microsoft.UI.WindowId? AppWindowId => _window?.AppWindow.Id;
    internal static SettingsPage RequestedPage => _requestedPage;
    internal static bool AllowClose => _allowClose;
    internal static event Action<SettingsPage>? PageRequested;

    public static void OpenOrActivate(SettingsPage page = SettingsPage.General)
    {
        _requestedPage = page;
        var existing = _window ?? ReactorApp.FindWindow(Key);
        if (existing != null)
        {
            _window = existing;
            PageRequested?.Invoke(page);
            existing.Activate();
            return;
        }

        _allowClose = false;
        var diagnostics = new ShellNavigationDiagnostics();
        ReactorWindow window;
        try
        {
            window = ReactorApp.OpenWindow(CreateSpec(), () => new SettingsView(diagnostics));
        }
        catch
        {
            diagnostics.WindowClosed("open-failed");
            throw;
        }
        _window = window;
        window.Closed += (_, _) =>
        {
            // Runs even when CloseIfOpen cleared _window before forced teardown.
            diagnostics.WindowClosed("window-event");
            if (!ReferenceEquals(_window, window))
                return;

            _window = null;
            _allowClose = false;
            PagurianLog.Host("settings: window closed");
        };
        window.Show();
        window.Activate();
        PagurianLog.Host($"settings: window opened page={page}");
    }

    internal static void RequestClose() => _window?.Close();

    // Used after an explicit discard confirmation. Queueing avoids closing
    // the native window from inside ContentDialog teardown.
    internal static void CloseWithoutPrompt()
    {
        _allowClose = true;
        ReactorApp.UIDispatcher?.TryEnqueue(() => _window?.Close());
    }

    public static void CloseIfOpen()
    {
        _allowClose = true;
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
        Width = 1600,
        Height = 900,
        Style = WindowStyle.Default,
        Backdrop = BackdropChoice.Of(BackdropKind.MicaAlt),
        ShowInTaskbar = true,
        ShowInSwitcher = true,
        NoActivate = false,
        IsMinimizable = true,
        IsMaximizable = true,
        ResizeMode = WindowResizeMode.CanResize,
        MinWidth = 800,
        MinHeight = 600,
        Level = WindowLevel.Normal,
        StartPosition = WindowStartPosition.CenterOnPrimary,
        Key = Key,
        Icon = WindowIcon.FromPath(AppAssets.IconPath),
    };
}

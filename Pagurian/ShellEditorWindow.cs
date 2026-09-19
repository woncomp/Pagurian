using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

namespace Pagurian;

// Owns the singleton Shell editor. It is a normal desktop window; the live
// taskbar tray remains visible and interactive until the draft is saved.
static class ShellEditorWindow
{
    private static readonly WindowKey Key = WindowKey.Of("pagurian-shell-editor");
    private static ReactorWindow? _window;
    private static bool _allowClose;
    private static bool _openSettingsAfterClose;

    internal static bool AllowClose => _allowClose;

    public static void OpenOrActivate()
    {
        SettingsWindow.CloseIfOpen();

        var existing = _window ?? ReactorApp.FindWindow(Key);
        if (existing != null)
        {
            _window = existing;
            existing.Activate();
            return;
        }

        _allowClose = false;
        _openSettingsAfterClose = false;
        var window = ReactorApp.OpenWindow(CreateSpec(), () => new ShellEditorView());
        _window = window;
        window.Closed += (_, _) => OnClosed(window);
        window.Show();
        window.Activate();
        PagurianLog.Host("shell editor: window opened");
    }

    internal static void RequestClose() => _window?.Close();

    // Settings requests are routed through the editor close guard. A dirty
    // editor can therefore keep editing without unexpectedly opening another
    // configuration window behind it.
    internal static void OpenSettingsOrActivate()
    {
        var existing = _window ?? ReactorApp.FindWindow(Key);
        if (existing == null)
        {
            SettingsWindow.OpenOrActivate();
            return;
        }

        _window = existing;
        _openSettingsAfterClose = true;
        existing.Close();
    }

    internal static void CancelPendingSettingsOpen() =>
        _openSettingsAfterClose = false;

    // Used after Save or an explicit discard confirmation. Queueing avoids
    // closing the native window from inside ContentDialog teardown.
    internal static void CloseWithoutPrompt()
    {
        _allowClose = true;
        ReactorApp.UIDispatcher?.TryEnqueue(() => _window?.Close());
    }

    // Process shutdown is already an explicit destructive action, so it must
    // not be blocked by a draft or open Settings afterward.
    public static void CloseIfOpen()
    {
        _allowClose = true;
        _openSettingsAfterClose = false;
        var existing = _window ?? ReactorApp.FindWindow(Key);
        _window = null;
        if (existing == null)
            return;

        try { existing.Close(); }
        catch { /* the native window may already be gone */ }
    }

    private static void OnClosed(ReactorWindow window)
    {
        if (!ReferenceEquals(_window, window))
            return;

        _window = null;
        _allowClose = false;
        var openSettings = _openSettingsAfterClose;
        _openSettingsAfterClose = false;
        PagurianLog.Host("shell editor: window closed");

        if (openSettings)
            ReactorApp.UIDispatcher?.TryEnqueue(SettingsWindow.OpenOrActivate);
    }

    private static WindowSpec CreateSpec() => new()
    {
        Title = "Pagurian Shell Editor",
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

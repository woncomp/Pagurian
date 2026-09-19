using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian;

// Shared visual policy for the interactive editor and the non-activating
// per-monitor blockers. Every surface remains opaque when Acrylic cannot be
// used, so adding a monitor can never expose readable desktop content.
static class ShellEditorBackdrop
{
    private static readonly SolidColorBrush DarkScrimBrush = new(
        Windows.UI.Color.FromArgb(0x88, 0, 0, 0));
    private static readonly SolidColorBrush LightScrimBrush = new(
        Windows.UI.Color.FromArgb(0x70, 255, 255, 255));

    public static bool DesktopAcrylicSupported
    {
        get
        {
            try { return DesktopAcrylicController.IsSupported(); }
            catch { return false; }
        }
    }

    public static bool AdvancedEffectsEnabled()
    {
        try { return new UISettings().AdvancedEffectsEnabled; }
        catch { return false; }
    }

    public static Element Apply(
        Element root,
        ColorScheme colorScheme,
        bool advancedEffects)
    {
        var useDesktopAcrylic = advancedEffects &&
            DesktopAcrylicSupported &&
            colorScheme != ColorScheme.HighContrast;

        root = useDesktopAcrylic
            ? root.Background(colorScheme == ColorScheme.Dark
                ? DarkScrimBrush
                : LightScrimBrush)
            : root.Background(Theme.Ref("SystemColorWindowColorBrush"));

        return root.Backdrop(useDesktopAcrylic
            ? BackdropKind.DesktopAcrylic
            : BackdropKind.None);
    }
}

// Secondary displays contain no editor controls. The non-null background is
// intentionally hit-testable while the native window remains NoActivate.
sealed class ShellEditorBackdropView : Component
{
    public override Element Render()
    {
        var colorScheme = UseColorScheme();
        var advancedEffects = UseMemo(
            ShellEditorBackdrop.AdvancedEffectsEnabled,
            Array.Empty<object>());

        return ShellEditorBackdrop.Apply(
            Border(null!)
                .AutomationName("Shell editor backdrop"),
            colorScheme,
            advancedEffects);
    }
}

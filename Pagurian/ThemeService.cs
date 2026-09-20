using Pagurian.Sdk;

namespace Pagurian;

// Host implementation of the theme contract. One instance per tray surface:
// the theme is derived from that surface's sampled taskbar luminance (never
// from registry/UISettings), so it always matches the actual taskbar the
// cells sit on. TextBrush is a shared live brush mutated in place on flips;
// Changed is the broadcast for components whose colors are computed per
// render. Shells receive the theme of the surface their tray is currently
// bound to; TrayManager re-points them when the binding changes.
sealed class ThemeService : IThemeService
{
    public ThemeService() { }

    public bool IsDark { get; private set; }

    public Microsoft.UI.Xaml.Media.SolidColorBrush TextBrush { get; } =
        new(TextColorFor(isDark: false));

    Microsoft.UI.Xaml.Media.Brush IThemeService.TextBrush => TextBrush;

    public event Action? Changed;

    // UI thread, from the owning surface's sampling loop. No-op unless the
    // theme actually flips.
    public void Apply(bool isDark)
    {
        if (isDark == IsDark)
            return;
        IsDark = isDark;
        TextBrush.Color = TextColorFor(isDark);
        Changed?.Invoke();
    }

    // TextFillColorPrimary: white on dark taskbars, near-black on light ones.
    public static Windows.UI.Color TextColorFor(bool isDark) =>
        isDark
            ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
            : Windows.UI.Color.FromArgb(255, 26, 26, 26);
}

using Pagurian.Sdk;

namespace Pagurian;

// Host implementation of the theme contract. The theme is derived from the
// sampled taskbar luminance by TaskbarController (never from registry/
// UISettings), so it always matches the actual taskbar. TextBrush is a shared
// live brush mutated in place on flips; Changed is the broadcast for
// components whose colors are computed per render.
sealed class ThemeService : IThemeService
{
    public static readonly ThemeService Instance = new();

    private ThemeService() { }

    public bool IsDark { get; private set; }

    public Microsoft.UI.Xaml.Media.SolidColorBrush TextBrush { get; } =
        new(TextColorFor(isDark: false));

    Microsoft.UI.Xaml.Media.Brush IThemeService.TextBrush => TextBrush;

    public event Action? Changed;

    // UI thread, from the controller's sampling loop. No-op unless the theme
    // actually flips.
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

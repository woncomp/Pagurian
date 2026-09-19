namespace Pagurian;

static class AppAssets
{
    // The Pagurian application icon used by the executable, host windows, and system tray.
    public static string ApplicationIconPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Pagurian-256.ico");

    // Kept only as the fallback for shell kinds that do not provide a preview icon.
    public static string ModuleFallbackIconPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Bokehlicia-Captiva-Atom.ico");
}

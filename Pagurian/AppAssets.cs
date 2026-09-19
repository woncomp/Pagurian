namespace Pagurian;

static class AppAssets
{
    // Kept as the fallback for shell kinds that do not provide a preview icon.
    public static string IconPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Bokehlicia-Captiva-Atom.ico");

    public static string TrayIconPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Pagurian-256.png");
}

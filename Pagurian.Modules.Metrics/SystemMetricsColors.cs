using Microsoft.UI.Xaml.Media;

namespace Pagurian.Modules.Metrics;

static class SystemMetricsColors
{
    public static Windows.UI.Color CpuAccent(bool isDark) =>
        isDark
            ? Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)
            : Windows.UI.Color.FromArgb(255, 0x10, 0x7C, 0x10);

    public static Windows.UI.Color MemoryAccent(bool isDark) =>
        isDark
            ? Windows.UI.Color.FromArgb(255, 0x5C, 0xC8, 0xFF)
            : Windows.UI.Color.FromArgb(255, 0x00, 0x5C, 0xA8);

    public static Windows.UI.Color GaugeTrack(bool isDark) =>
        isDark
            ? Windows.UI.Color.FromArgb(0x30, 255, 255, 255)
            : Windows.UI.Color.FromArgb(0x30, 0, 0, 0);

    public static Brush CpuAccentBrush(bool isDark) => new SolidColorBrush(CpuAccent(isDark));

    public static Brush MemoryAccentBrush(bool isDark) => new SolidColorBrush(MemoryAccent(isDark));

    public static Brush GaugeTrackBrush(bool isDark) => new SolidColorBrush(GaugeTrack(isDark));
}

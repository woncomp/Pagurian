using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Pagurian.Modules.Copilot;

// UI-thread only. Measure with WinUI itself, using exactly the style used by
// both rendered captions. Do not use the SDK's cached text-scale measurement.
internal static class SessionCellLayout
{
    internal const double LargeWidthScale = 1.6;
    internal const double IconDip = 16;
    internal const double GapDip = 4;
    internal const double PaddingDip = 8;
    internal const string CaptionStyle = "CaptionTextBlockStyle";

    internal static double MeasureWidth(CopilotSessionIconSize iconSize = CopilotSessionIconSize.Medium)
    {
        var text = new TextBlock
        {
            Style = (Style)Application.Current.Resources[CaptionStyle],
        };
        double longest = 0;
        foreach (string status in new[] { "Unknown", "Idle", "Working", "Blocked" })
        {
            text.Text = status;
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            longest = Math.Max(longest, text.DesiredSize.Width);
        }
        double medium = Math.Ceiling((longest + IconDip + GapDip + PaddingDip * 2) / 4) * 4;
        return iconSize == CopilotSessionIconSize.Large
            ? medium * LargeWidthScale
            : medium;
    }
}

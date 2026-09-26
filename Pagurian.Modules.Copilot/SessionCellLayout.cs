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

    internal static double LargeWidth => MeasureWidth(CopilotSessionIconSize.Large);

    internal static double LargeProjectNameWidth => LargeWidth -
        2 * (PaddingDip + IconDip + GapDip);

    internal static double LargeNameWidthWithBoundaries => LargeWidth -
        MeasureCaptionWidth("[") - MeasureCaptionWidth("]");

    internal static double MeasureWidth(CopilotSessionIconSize iconSize = CopilotSessionIconSize.Medium)
    {
        double longest = new[] { "Unknown", "Idle", "Working", "Blocked" }
            .Select(MeasureCaptionWidth)
            .Max();
        double medium = Math.Ceiling((longest + IconDip + GapDip + PaddingDip * 2) / 4) * 4;
        return iconSize == CopilotSessionIconSize.Large
            ? medium * LargeWidthScale
            : medium;
    }

    internal static bool HasRoomForLargeNameBoundaries(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return MeasureCaptionWidth(name) <= LargeNameWidthWithBoundaries;
    }

    private static double MeasureCaptionWidth(string value)
    {
        var text = new TextBlock
        {
            Style = (Style)Application.Current.Resources[CaptionStyle],
            Text = value,
        };
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return text.DesiredSize.Width;
    }
}

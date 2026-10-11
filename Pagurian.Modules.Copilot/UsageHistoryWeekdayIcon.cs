using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Core.V1Protocol.Descriptor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Pagurian.Modules.Copilot;

internal sealed record UsageHistoryWeekdayIconElement : Element
{
    internal Action<UsageHistoryWeekdayIcon>[] Setters { get; init; } = [];
    static UsageHistoryWeekdayIconElement() =>
        ControlRegistry.Register<UsageHistoryWeekdayIconElement, UsageHistoryWeekdayIcon>(
            static () => new DescriptorHandler<UsageHistoryWeekdayIconElement, UsageHistoryWeekdayIcon>(
                new ControlDescriptor<UsageHistoryWeekdayIconElement, UsageHistoryWeekdayIcon>
                {
                    GetSetters = static element => element.Setters,
                }));
    internal UsageHistoryWeekdayIconElement Set(Action<UsageHistoryWeekdayIcon> action) =>
        this with { Setters = [action] };
}

internal sealed class UsageHistoryWeekdayIcon : UserControl
{
    // Approved single-color seven-luminary artwork: 24-unit grid, 2-unit round strokes.
    private static readonly string[] Paths =
    [
        "M16 12A4 4 0 1 0 8 12A4 4 0 1 0 16 12Z " +
            "M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4" +
            "M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4",
        "M20.1 14.2A8.5 8.5 0 1 1 9.8 3.9M9.8 3.9A6.9 6.9 0 0 0 20.1 14.2",
        "M15 14.5A5.5 5.5 0 1 0 4 14.5A5.5 5.5 0 1 0 15 14.5Z " +
            "M13.4 10.6 20 4M14.5 4H20v5.5",
        "M16 10.5A4 4 0 1 0 8 10.5A4 4 0 1 0 16 10.5Z " +
            "M7.5 3Q7.5 6.5 12 6.5T16.5 3M12 14.5V21M8.5 18h7",
        "M5.5 5.5C10 2 13 4.5 10 9l-4.5 5.5H19M15 3.5V21",
        "M17.5 8.5A5.5 5.5 0 1 0 6.5 8.5A5.5 5.5 0 1 0 17.5 8.5Z " +
            "M12 14v7M8.5 18h7",
        "M8 3v18M4.5 7h7M8 13c6-6 11-3 8 2l-2 3c-.7 1.3.4 2.5 2 2.5",
    ];

    private readonly XamlPath _path = new()
    {
        Width = 24,
        Height = 24,
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    internal DayOfWeek Weekday { get; private set; }

    public UsageHistoryWeekdayIcon()
    {
        Width = Height = 14;
        HorizontalAlignment = HorizontalAlignment.Center;
        IsHitTestVisible = false;
        IsTabStop = false;
        var canvas = new Canvas { Width = 24, Height = 24 };
        var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
        AutomationProperties.SetAccessibilityView(viewbox, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(canvas, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_path, AccessibilityView.Raw);
        canvas.Children.Add(_path);
        Content = viewbox;
    }

    internal void Update(DayOfWeek weekday, bool dark, bool highContrast)
    {
        if (!Enum.IsDefined(weekday))
            throw new ArgumentOutOfRangeException(nameof(weekday));
        if (_path.Data is null || Weekday != weekday)
        {
            _path.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), Paths[(int)weekday]);
            Weekday = weekday;
        }
        _path.Stroke = highContrast
            ? UsageTint.SystemBrush("SystemColorWindowTextColorBrush")
            : UsageBalanceTooltip.ThemeBrush("TextFillColorPrimaryBrush", dark);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new WeekdayIconAutomationPeer(this);

    private sealed class WeekdayIconAutomationPeer(UsageHistoryWeekdayIcon owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(UsageHistoryWeekdayIcon);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
}

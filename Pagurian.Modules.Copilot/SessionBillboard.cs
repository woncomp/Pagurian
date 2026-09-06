using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;

namespace Pagurian.Modules.Copilot;

// Billboard for one Copilot session, opened by clicking its tray cell: the
// session name, its colored status, and the last received hook event dumped
// as pretty-printed JSON. Re-renders on every tracker change, so the status
// and dump stay live while the billboard is open. The host closes it when
// the session ends (its owner cell is removed).
class SessionBillboard : Billboard
{
    private readonly CopilotSession _session;

    public SessionBillboard(CopilotSession session) => _session = session;

    public override double WidthDip => 420;

    public override double HeightDip => 360;

    public override string Title => "Copilot Session";

    public override Element Render()
    {
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        var colorScheme = UseColorScheme();

        UseEffect(() =>
        {
            void OnChanged() => setVersion(++tick.Current);
            CopilotSessionTracker.UiChanged += OnChanged;
            Theme.Changed += OnChanged;
            return () =>
            {
                CopilotSessionTracker.UiChanged -= OnChanged;
                Theme.Changed -= OnChanged;
            };
        }, Array.Empty<object>());

        var highContrast = colorScheme == ColorScheme.HighContrast;
        var requestedTheme = highContrast
            ? ElementTheme.Default
            : Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        var ended = CopilotSessionTracker.Find(_session.SessionId) == null;

        var header = Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            [GridSize.Auto, GridSize.Auto],
            [
                Image(CopilotModule.GitHubIconPath)
                    .Width(32)
                    .Height(32)
                    .AccessibilityHidden()
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0, rowSpan: 2),
                Subtitle(_session.Name)
                    .HeadingLevel(AutomationHeadingLevel.Level1)
                    .MaxLines(1)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .ToolTip(_session.Name)
                    .Margin(12, 0, 0, 0)
                    .Grid(row: 0, column: 1),
                Caption("Copilot session")
                    .Foreground(ReactorTheme.SecondaryText)
                    .Margin(12, 4, 0, 0)
                    .Grid(row: 1, column: 1),
                StatusBadge(
                        ended ? "Ended" : _session.Status.ToString(),
                        ended ? CopilotSessionStatus.Idle : _session.Status,
                        highContrast)
                    .Margin(12, 0, 0, 0)
                    .VAlign(VerticalAlignment.Top)
                    .Grid(row: 0, column: 2, rowSpan: 2),
            ]);

        Element eventContent;
        if (ended)
        {
            eventContent = Body("Session ended.")
                .Foreground(ReactorTheme.SecondaryText);
        }
        else
        {
            var eventName = string.IsNullOrWhiteSpace(_session.LastEventName)
                ? "Waiting for an event"
                : _session.LastEventName;
            var eventDump = string.IsNullOrWhiteSpace(_session.LastEventDump)
                ? "No event received yet."
                : _session.LastEventDump;

            eventContent = VStack(8,
                VStack(4,
                    BodyStrong("Latest event")
                        .HeadingLevel(AutomationHeadingLevel.Level2),
                    Caption(eventName)
                        .Foreground(ReactorTheme.SecondaryText)),
                Caption(eventDump)
                    .FontFamily("Consolas")
                    .TextWrapping(TextWrapping.Wrap));
        }

        var content = FlexColumn(
            header,
            (ScrollViewer(MaterialCard(eventContent, highContrast)) with
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollMode = ScrollMode.Disabled,
            })
                .Margin(0, 8, 0, 0)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .Flex(1));

        return Page(content, highContrast)
            .RequestedTheme(requestedTheme);
    }

    private static BorderElement StatusBadge(
        string label,
        CopilotSessionStatus status,
        bool highContrast)
    {
        var foreground = highContrast
            ? ReactorTheme.Ref("SystemColorWindowTextColorBrush")
            : status switch
            {
                CopilotSessionStatus.Working => ReactorTheme.SystemSuccess,
                CopilotSessionStatus.Blocked => ReactorTheme.SystemCaution,
                _ => ReactorTheme.SystemNeutral,
            };
        var background = highContrast
            ? ReactorTheme.Ref("SystemColorWindowColorBrush")
            : status switch
            {
                CopilotSessionStatus.Working => ReactorTheme.SystemSuccessBackground,
                CopilotSessionStatus.Blocked => ReactorTheme.SystemCautionBackground,
                _ => ReactorTheme.SystemNeutralBackground,
            };

        return Border(
                Caption(label)
                    .SemiBold()
                    .Foreground(foreground))
            .Padding(8, 4)
            .CornerRadius(4)
            .Background(background)
            .WithBorder(
                highContrast
                    ? ReactorTheme.Ref("SystemColorWindowTextColorBrush")
                    : foreground,
                highContrast ? 2 : 1)
            .Set(border => border.BackgroundSizing = BackgroundSizing.InnerBorderEdge);
    }

    private static BorderElement Page(Element content, bool highContrast)
    {
        var page = Border(content)
            .Padding(8)
            .CornerRadius(8);

        return highContrast
            ? page
                .Background(ReactorTheme.Ref("SystemColorWindowColorBrush"))
                .WithBorder(ReactorTheme.Ref("SystemColorWindowTextColorBrush"), 2)
                .Set(border => border.BackgroundSizing = BackgroundSizing.InnerBorderEdge)
            : page;
    }

    private static BorderElement MaterialCard(Element content, bool highContrast) =>
        Border(content)
            .Padding(12)
            .CornerRadius(8)
            .Background(highContrast
                ? ReactorTheme.Ref("SystemColorWindowColorBrush")
                : ReactorTheme.Ref("LayerOnAcrylicFillColorDefaultBrush"))
            .WithBorder(
                highContrast
                    ? ReactorTheme.Ref("SystemColorWindowTextColorBrush")
                    : ReactorTheme.SurfaceStroke,
                highContrast ? 2 : 1)
            .Set(border => border.BackgroundSizing = BackgroundSizing.InnerBorderEdge);
}

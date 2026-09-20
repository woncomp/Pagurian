using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;

namespace Pagurian.Modules.Copilot;

// A shared status-sized width, never a current-status- or project-sized width.
class SessionCell : ShellCell
{
    public override Element Render()
    {
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        var colorScheme = UseColorScheme();
        var cellModel = Model as CopilotSessionCellModel;
        var session = cellModel?.Session ?? ModelAs<CopilotSession>();
        var iconSize = cellModel?.IconSize ?? CopilotSessionIconSize.Medium;

        UseEffect(() =>
        {
            var settings = new UISettings();
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            void OnChanged() => setVersion(++tick.Current);
            void OnTextScaleChanged(UISettings sender, object args) => dispatcher.TryEnqueue(OnChanged);
            CopilotSessionTracker.UiChanged += OnChanged;
            if (session.IsSdk)
                CopilotModule.Instance.SdkSessions.UiChanged += OnChanged;
            session.Changed += OnChanged;
            if (cellModel is not null)
                cellModel.Changed += OnChanged;
            Theme.Changed += OnChanged;
            settings.TextScaleFactorChanged += OnTextScaleChanged;
            return () =>
            {
                CopilotSessionTracker.UiChanged -= OnChanged;
                if (session.IsSdk)
                    CopilotModule.Instance.SdkSessions.UiChanged -= OnChanged;
                session.Changed -= OnChanged;
                if (cellModel is not null)
                    cellModel.Changed -= OnChanged;
                Theme.Changed -= OnChanged;
                settings.TextScaleFactorChanged -= OnTextScaleChanged;
            };
        }, Array.Empty<object>());

        bool highContrast = colorScheme == ColorScheme.HighContrast;
        Brush statusBrush = highContrast
            ? (Brush)Application.Current.Resources["SystemColorWindowTextColorBrush"]
            : CopilotStatusColors.StatusBrushFor(session, Theme.IsDark);
        Element content = iconSize == CopilotSessionIconSize.Large
            ? LargeContent(session, statusBrush)
            : MediumContent(session, highContrast, statusBrush);

        return Border(content)
            .Width(SessionCellLayout.MeasureWidth(iconSize))
            .Padding(SessionCellLayout.PaddingDip, 0)
            .RequestedTheme(highContrast ? ElementTheme.Default
                : Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light)
            .AutomationName($"{session.Name}, {session.Status}, {session.ProjectName}");
    }

    private static Element MediumContent(
        CopilotSession session,
        bool highContrast,
        Brush statusBrush)
    {
        var status = TextBlock(session.Status.ToString())
            .ApplyStyle(SessionCellLayout.CaptionStyle)
            .VAlign(VerticalAlignment.Center)
            .Foreground(statusBrush);

        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto],
                HStack(SessionCellLayout.GapDip,
                    Image(CopilotModule.ClientIconPath(session.Client))
                        .Width(SessionCellLayout.IconDip)
                        .Height(SessionCellLayout.IconDip)
                        .AccessibilityHidden()
                        .VAlign(VerticalAlignment.Center),
                    status)
                    .HAlign(HorizontalAlignment.Center)
                    .Grid(row: 0),
                TextBlock(session.ProjectName)
                    .ApplyStyle(SessionCellLayout.CaptionStyle)
                    .MaxLines(1)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .TextAlignment(TextAlignment.Center)
                    .Foreground(highContrast
                        ? ReactorTheme.Ref("SystemColorWindowTextColorBrush")
                        : ReactorTheme.PrimaryText)
                    .Grid(row: 1))
                .VAlign(VerticalAlignment.Center);
    }

    private static Element LargeContent(CopilotSession session, Brush statusBrush) =>
        Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            [GridSize.Auto, GridSize.Auto],
            Image(CopilotModule.ClientIconPath(session.Client))
                .Width(SessionCellLayout.IconDip)
                .Height(SessionCellLayout.IconDip)
                .AccessibilityHidden()
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 0),
            TextBlock(session.ProjectName)
                .ApplyStyle(SessionCellLayout.CaptionStyle)
                .MaxLines(1)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .ToolTip(session.ProjectName)
                .Margin(SessionCellLayout.GapDip, 0)
                .HAlign(HorizontalAlignment.Stretch)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 1),
            StatusIcon(session.Status, statusBrush)
                .Margin(left: SessionCellLayout.GapDip, top: 0, right: 0, bottom: 0)
                .Grid(row: 0, column: 2),
            TextBlock(session.Name)
                .ApplyStyle(SessionCellLayout.CaptionStyle)
                .MaxLines(1)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .ToolTip(session.Name)
                .Grid(row: 1, column: 0, columnSpan: 3));

    private static Element StatusIcon(CopilotSessionStatus status, Brush brush) =>
        status == CopilotSessionStatus.Working
            ? ProgressRing()
                .IsActive()
                .Width(SessionCellLayout.IconDip)
                .Height(SessionCellLayout.IconDip)
                .Foreground(brush)
                .AccessibilityHidden()
            : Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                Ellipse()
                    .Set(ellipse =>
                    {
                        ellipse.Stroke = brush;
                        ellipse.StrokeThickness = 1.5;
                    })
                    .Width(SessionCellLayout.IconDip)
                    .Height(SessionCellLayout.IconDip),
                status switch
                {
                    CopilotSessionStatus.Blocked => Caption("!")
                        .Foreground(brush)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center),
                    CopilotSessionStatus.Unknown => Caption("?")
                        .Foreground(brush)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center),
                    _ => null,
                })
                .Width(SessionCellLayout.IconDip)
                .Height(SessionCellLayout.IconDip)
                .AccessibilityHidden();
}

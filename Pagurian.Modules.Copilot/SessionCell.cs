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
        bool simple = cellModel?.IsSimpleMode == true;
        Element content = simple
            ? SimpleContent(session, highContrast)
            : iconSize == CopilotSessionIconSize.Large
                ? LargeContent(session, statusBrush)
                : MediumContent(session, highContrast, statusBrush);

        return Border(content)
            .Width(simple ? SessionCellLayout.SimpleWidth : SessionCellLayout.MeasureWidth(iconSize))
            .Padding(simple ? SessionCellLayout.SimplePaddingDip
                : iconSize == CopilotSessionIconSize.Large ? 0 : SessionCellLayout.PaddingDip, 0)
            .RequestedTheme(highContrast ? ElementTheme.Default
                : Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light)
            .AutomationName($"{session.Name}, {session.Status}, {session.ProjectName}");
    }

    private static Element SimpleContent(CopilotSession session, bool highContrast) =>
        Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto],
            Image(CopilotModule.ClientIconPath(session.Client))
                .Width(SessionCellLayout.IconDip)
                .Height(SessionCellLayout.IconDip)
                .AccessibilityHidden()
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0),
            TextBlock(session.ProjectName)
                .ApplyStyle(SessionCellLayout.CaptionStyle)
                .MaxLines(1)
                .TextWrapping(TextWrapping.NoWrap)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .TextAlignment(TextAlignment.Center)
                .Foreground(highContrast
                    ? ReactorTheme.Ref("SystemColorWindowTextColorBrush")
                    : ReactorTheme.PrimaryText)
                .Grid(row: 1))
            .VAlign(VerticalAlignment.Center);

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
                    .TextWrapping(TextWrapping.NoWrap)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .TextAlignment(TextAlignment.Center)
                    .Foreground(highContrast
                        ? ReactorTheme.Ref("SystemColorWindowTextColorBrush")
                        : ReactorTheme.PrimaryText)
                    .Grid(row: 1))
                .VAlign(VerticalAlignment.Center);
    }

    private static Element LargeContent(CopilotSession session, Brush statusBrush) =>
        (Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Auto],
            Image(CopilotModule.ClientIconPath(session.Client))
                .Width(SessionCellLayout.IconDip)
                .Height(SessionCellLayout.IconDip)
                .Margin(left: SessionCellLayout.PaddingDip, top: 0, right: 0, bottom: 0)
                .AccessibilityHidden()
                .HAlign(HorizontalAlignment.Left)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0),
            LargeProjectName(session.ProjectName)
                .Grid(row: 0),
            SessionStatusIcon.For(session.Status, statusBrush)
                .Margin(left: SessionCellLayout.GapDip, top: 0,
                    right: SessionCellLayout.PaddingDip, bottom: 0)
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0),
            LargeName(session.Name)
                .Grid(row: 1)) with { RowSpacing = 2 })
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Center);

    private static Element LargeProjectName(string projectName)
    {
        var label = TextBlock(projectName)
            .ApplyStyle(SessionCellLayout.CaptionStyle)
            .MaxLines(1)
            .TextWrapping(TextWrapping.NoWrap)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .TextAlignment(TextAlignment.Center)
            .ToolTip(projectName)
            .Width(SessionCellLayout.LargeProjectNameWidthWithBoundaries)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);

        return Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            TextBlock("[")
                .ApplyStyle(SessionCellLayout.CaptionStyle)
                .AccessibilityHidden()
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 0),
            label.Grid(row: 0, column: 1),
            TextBlock("]")
                .ApplyStyle(SessionCellLayout.CaptionStyle)
                .AccessibilityHidden()
                .HAlign(HorizontalAlignment.Right)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 2))
            .Width(SessionCellLayout.LargeWidth)
            .VAlign(VerticalAlignment.Center);
    }

    private static Element LargeName(string name) =>
        TextBlock(name)
            .ApplyStyle(SessionCellLayout.CaptionStyle)
            .MaxLines(1)
            .TextWrapping(TextWrapping.NoWrap)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .TextAlignment(TextAlignment.Center)
            .ToolTip(name)
            .Width(SessionCellLayout.LargeWidth)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);

}

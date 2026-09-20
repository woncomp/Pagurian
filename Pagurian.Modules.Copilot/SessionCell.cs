using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
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

        UseEffect(() =>
        {
            var settings = new UISettings();
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            void OnChanged() => setVersion(++tick.Current);
            void OnTextScaleChanged(UISettings sender, object args) => dispatcher.TryEnqueue(OnChanged);
            CopilotSessionTracker.UiChanged += OnChanged;
            Theme.Changed += OnChanged;
            settings.TextScaleFactorChanged += OnTextScaleChanged;
            return () =>
            {
                CopilotSessionTracker.UiChanged -= OnChanged;
                Theme.Changed -= OnChanged;
                settings.TextScaleFactorChanged -= OnTextScaleChanged;
            };
        }, Array.Empty<object>());

        var session = ModelAs<CopilotSession>();
        bool highContrast = colorScheme == ColorScheme.HighContrast;
        var status = TextBlock(session.Status.ToString())
            .ApplyStyle(SessionCellLayout.CaptionStyle)
            .VAlign(VerticalAlignment.Center);
        status = highContrast
            ? status.Foreground(ReactorTheme.Ref("SystemColorWindowTextColorBrush"))
            : status.Foreground(CopilotStatusColors.StatusBrushFor(session, Theme.IsDark));

        return Border(
                Grid([GridSize.Star()], [GridSize.Auto, GridSize.Auto],
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
                    .VAlign(VerticalAlignment.Center))
            .Width(SessionCellLayout.MeasureWidth())
            .Padding(SessionCellLayout.PaddingDip, 0)
            .RequestedTheme(highContrast ? ElementTheme.Default
                : Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light)
            .AutomationName($"{session.Name}, {session.Status}, {session.ProjectName}");
    }
}

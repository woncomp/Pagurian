using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Copilot;

// One cell per tracked Copilot session: GitHub icon + colored status text,
// centered as a group. The model is the live CopilotSession object; the cell
// re-renders on every tracker change and taskbar theme flip.
class SessionCell : ShellCell
{
    private const double FontSizeDip = 12;
    private const double PaddingXDip = 8;

    public override Element Render()
    {
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);

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

        var session = ModelAs<CopilotSession>();

        return Border(
                HStack(6,
                [
                    Image(CopilotModule.GitHubIconPath)
                        .Width(16)
                        .Height(16)
                        .AccessibilityHidden()
                        .VerticalAlignment(VerticalAlignment.Center),
                    TextBlock(session.Status.ToString())
                        .FontSize(FontSizeDip)
                        .Foreground(CopilotStatusColors.StatusBrushFor(session, Theme.IsDark))
                        .VerticalAlignment(VerticalAlignment.Center),
                ])
                .HorizontalAlignment(HorizontalAlignment.Center)
                .VerticalAlignment(VerticalAlignment.Center))
            // Sizes to the icon + status text (status strings are bounded
            // enum names); the host reads the rendered width back.
            .Padding(PaddingXDip, 0, PaddingXDip, 0);
    }
}

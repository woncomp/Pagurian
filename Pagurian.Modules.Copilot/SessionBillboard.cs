using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Copilot;

// Billboard for one Copilot session, opened by clicking its tray cell: the
// session name, its colored status, and the last received hook event dumped
// as pretty-printed JSON. Re-renders on every tracker change, so the status
// and dump stay live while the billboard is open. The host closes it when
// the session ends (its owner cell is removed).
class SessionBillboard : Billboard
{
    // Fixed dump area height: window minus padding, name row, status row and
    // margins (see Render).
    private const double DumpHeightDip = 164;

    private readonly CopilotSession _session;

    public SessionBillboard(CopilotSession session) => _session = session;

    public override double WidthDip => 360;

    public override double HeightDip => 260;

    public override string Title => "Copilot Session";

    public override Element Render()
    {
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);

        // Live updates: re-render whenever any session changes state or the
        // taskbar theme flips.
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

        if (CopilotSessionTracker.Find(_session.SessionId) == null)
            // The session ended and the tracker dropped it; the host closes
            // this billboard on its next tick.
            return TextBlock("Session ended.").Padding(14);

        return FlexColumn(
                FlexRow(
                    Image(CopilotModule.GitHubIconPath)
                        .Width(24)
                        .Height(24)
                        .AccessibilityHidden()
                        .VerticalAlignment(VerticalAlignment.Center),
                    TextBlock(_session.Name)
                        .FontSize(14)
                        .SemiBold()
                        .MaxLines(1)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .Margin(10, 0, 0, 0)
                        .VerticalAlignment(VerticalAlignment.Center)),
                FlexRow(
                    TextBlock("Status:")
                        .FontSize(12),
                    TextBlock(_session.Status.ToString())
                        .FontSize(12)
                        .SemiBold()
                        .Foreground(CopilotStatusColors.StatusBrushFor(_session, Theme.IsDark))
                        .Margin(6, 0, 0, 0))
                    .Margin(0, 8, 0, 0),
                ScrollViewer(
                    TextBlock(_session.LastEventDump)
                        .FontFamily("Consolas")
                        .FontSize(11)
                        .TextWrapping(TextWrapping.Wrap))
                    .Height(DumpHeightDip)
                    .Margin(0, 10, 0, 0))
            .Padding(14);
    }
}

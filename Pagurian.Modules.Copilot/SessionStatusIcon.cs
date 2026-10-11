using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Copilot;

// The 16x16 status-icon vocabulary shared by the tray cell and the billboard
// session tree: a spinning ring for Working, otherwise a hollow ring with an
// overlaid glyph. Terminal/unresolved tree nodes (any non-Active lifecycle)
// collapse to one uniform inactive mark: grey ring + ✕.
static class SessionStatusIcon
{
    internal static Element For(CopilotSessionStatus status, Brush brush) =>
        status == CopilotSessionStatus.Working
            ? ProgressRing()
                .IsActive()
                .Width(SessionCellLayout.IconDip)
                .Height(SessionCellLayout.IconDip)
                .Foreground(brush)
                .AccessibilityHidden()
            : Ring(brush, status switch
            {
                CopilotSessionStatus.Blocked => "!",
                CopilotSessionStatus.Done => "✓",
                CopilotSessionStatus.Unknown => "?",
                _ => null,
            });

    // Tree-node variant: an Active lifecycle keeps the live status icon;
    // anything terminal or unresolved is the uniform inactive mark.
    internal static Element ForNode(CopilotSessionNode node, Brush statusBrush, Brush inactiveBrush) =>
        node.Lifecycle == "Active"
            ? For(node.Status ?? CopilotSessionStatus.Unknown, statusBrush)
            : Ring(inactiveBrush, "✕");

    private static Element Ring(Brush brush, string? glyph) =>
        Grid(
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
            glyph is null
                ? null
                : Caption(glyph)
                    .Foreground(brush)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center))
            .Width(SessionCellLayout.IconDip)
            .Height(SessionCellLayout.IconDip)
            .AccessibilityHidden();
}

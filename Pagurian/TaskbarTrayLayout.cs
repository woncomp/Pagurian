using Microsoft.UI.Reactor.Input;

namespace Pagurian;

// Single source of truth for the tray window's cell layout: one CellInfo per
// widget cell (clock, CPU, memory, then one per tracked Copilot session, in
// the same left-to-right order TaskbarTrayWindow renders them).
//
// Widths are READ BACK from the rendered cells: Render binds each cell
// Border to an ElementRef (RefFor) and the widths come from the mounted
// controls' ActualWidth — so the layout matches what's on screen by
// construction, with no per-widget text-measuring rules to keep in sync with
// the render code. Until a cell has mounted and laid out (the window is
// sized before XAML's first layout pass) its width reads as 0: the window
// starts at a 1-DIP floor and the controller's 50 ms anchor loop grows it to
// the real content size within a tick or two. No startup estimates to keep
// in sync with the widget set — correct for any combination of widgets.
//
// InnerWidthDip is the Border's own width (content + padding, no margin);
// CellWidthDip adds the hover margins on both sides and is the hit-test slot
// the controller walks when building its per-cell rects.
//
// Both consumers run on the UI thread (Render and the controller's poll
// tick), so no locking. This class must never raise events or set component
// state: Render binds the refs and the controller resizes the window on its
// next tick, with no feedback loop.
sealed record CellInfo(string Id, double InnerWidthDip, double CellWidthDip);

static class TaskbarTrayLayout
{
    // One imperative ref per cell Border, bound in Render via .Ref(). A
    // static cache like the window's brush caches, since session cells come
    // and go; refs for ended sessions are dropped from Render (PruneRefs).
    private static readonly Dictionary<string, ElementRef> _cellRefs = new();

    public static ElementRef RefFor(string id)
    {
        if (!_cellRefs.TryGetValue(id, out var r))
        {
            r = new ElementRef();
            _cellRefs[id] = r;
        }
        return r;
    }

    public static IReadOnlyList<CellInfo> Cells
    {
        get
        {
            var cells = new List<CellInfo>
            {
                Cell(TaskbarTrayWindow.ClockWidgetId),
                Cell(TaskbarTrayWindow.CpuWidgetId),
                Cell(TaskbarTrayWindow.MemoryWidgetId),
            };
            foreach (var session in CopilotSessionTracker.Sessions)
                cells.Add(Cell(session.SessionId));
            return cells;
        }
    }

    public static double TotalWidthDip
    {
        get
        {
            double total = 0;
            foreach (var cell in Cells)
                total += cell.CellWidthDip;
            return total;
        }
    }

    // Drop refs whose session is gone (sessionEnd removes sessions now, so
    // without this the cache would grow forever). Runs on the UI thread from
    // Render, next to the brush-cache pruning.
    public static void PruneRefs()
    {
        foreach (var id in _cellRefs.Keys
                     .Where(k => k != TaskbarTrayWindow.ClockWidgetId
                              && k != TaskbarTrayWindow.CpuWidgetId
                              && k != TaskbarTrayWindow.MemoryWidgetId
                              && CopilotSessionTracker.Find(k) == null)
                     .ToList())
            _cellRefs.Remove(id);
    }

    // Width of one cell: its rendered ActualWidth once mounted and laid out,
    // 0 before that (an unmounted cell can't be hovered, so a zero hit-test
    // slot is harmless; the window grows from the 1-DIP floor within a tick
    // or two of the first render).
    private static CellInfo Cell(string id)
    {
        var actual = _cellRefs.TryGetValue(id, out var r) ? r.Current?.ActualWidth ?? 0 : 0;
        var inner = actual > 0 ? actual : 0;
        return new CellInfo(id, inner, inner + 2 * TaskbarTrayWindow.HoverMarginXDip);
    }
}

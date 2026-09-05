using Microsoft.UI.Reactor.Input;

namespace Pagurian;

// Single source of truth for the tray window's cell layout: one CellInfo per
// cell of every configured shell, in tray order (shells in config order,
// their cells in AddCell order).
//
// Widths are READ BACK from the rendered cells: the window binds each cell
// Border to an ElementRef (RefFor) and the widths come from the mounted
// controls' ActualWidth — so the layout matches what's on screen by
// construction. Until a cell has mounted and laid out its width reads as 0:
// the window starts at a 1-DIP floor and the controller's 50 ms anchor loop
// grows it to the real content size within a tick or two.
//
// Both consumers run on the UI thread (Render and the controller's poll
// tick), so no locking. The controller reports measured-width changes back
// through the guarded event below; stable measurements raise nothing, which
// prevents a render/resize feedback loop.
sealed record CellInfo(string Key, double InnerWidthDip, double CellWidthDip);

static class TaskbarTrayLayout
{
    private const double WidthChangeToleranceDip = 0.5;

    // One imperative ref per cell Border, bound in Render via .Ref(). A
    // static cache like the window's brush caches, since cells come and go;
    // refs for removed cells are dropped from Render (PruneRefs).
    private static readonly Dictionary<string, ElementRef> _cellRefs = new();
    private static double _lastReportedTotalWidthDip = double.NaN;

    // Raised after the controller observes a new measured total width. The
    // tray window uses this to replace the provisional width from the render
    // that first mounted a cell with its final ActualWidth-based width.
    public static event Action? MeasuredWidthChanged;

    public static ElementRef RefFor(string key)
    {
        if (!_cellRefs.TryGetValue(key, out var r))
        {
            r = new ElementRef();
            _cellRefs[key] = r;
        }
        return r;
    }

    public static IReadOnlyList<CellInfo> Cells
    {
        get
        {
            var cells = new List<CellInfo>();
            foreach (var cell in TrayShells.Cells)
                cells.Add(Cell(cell.Key));
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

    // Called by the controller's normal anchor pass after it has read all
    // mounted cell refs. Stable measurements do not raise another event, so
    // the resulting re-render cannot form a render/resize feedback loop.
    public static void ReportMeasuredWidth(double totalWidthDip)
    {
        if (!double.IsNaN(_lastReportedTotalWidthDip) &&
            Math.Abs(totalWidthDip - _lastReportedTotalWidthDip) <= WidthChangeToleranceDip)
        {
            return;
        }

        _lastReportedTotalWidthDip = totalWidthDip;
        MeasuredWidthChanged?.Invoke();
    }

    // Drop refs whose cell is gone (dynamic shells remove cells), or the
    // cache would grow forever. Runs on the UI thread from Render, next to
    // the hover-brush usage.
    public static void PruneRefs()
    {
        foreach (var key in _cellRefs.Keys
                     .Where(k => TrayShells.FindCell(k) == null)
                     .ToList())
            _cellRefs.Remove(key);
    }

    // Width of one cell: its rendered ActualWidth once mounted and laid out,
    // 0 before that (an unmounted cell can't be hovered, so a zero hit-test
    // slot is harmless; the window grows from the 1-DIP floor within a tick
    // or two of the first render).
    private static CellInfo Cell(string key)
    {
        var actual = _cellRefs.TryGetValue(key, out var r) ? r.Current?.ActualWidth ?? 0 : 0;
        var inner = actual > 0 ? actual : 0;
        return new CellInfo(key, inner, inner + 2 * TaskbarTrayWindow.HoverMarginXDip);
    }
}

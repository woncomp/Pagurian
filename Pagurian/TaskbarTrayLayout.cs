using System.Collections.Immutable;
using Microsoft.UI.Reactor.Input;
using Pagurian.Sdk;

namespace Pagurian;

internal sealed record TrayCellLayout(string Key, double WidthDip, TaskbarInterop.RECT BoundsPx);
internal sealed record TrayLayoutSnapshot(long Version, ImmutableArray<TrayCellLayout> Cells,
    double WidthDip, double Scale, double WindowScale, TaskbarInterop.RECT BoundsPx);

// References belong to one window, not to a global polling feedback loop.
internal sealed class TaskbarTrayLayout
{
    private readonly Dictionary<string, ElementRef> _refs = new();
    private ShellCellHandle[] _cells = [];
    internal int MountCount { get; private set; }
    internal int UnmountCount { get; private set; }
    internal event Action<string>? Diagnostic;
    internal void Mounted(string key)
    {
        MountCount++;
        TaskbarTrayWindow.BrushMounted(key);
        Diagnostic?.Invoke($"mount key={key}");
    }
    internal void Unmounted(string key)
    {
        UnmountCount++;
        TaskbarTrayWindow.BrushUnmounted(key);
        Diagnostic?.Invoke($"unmount key={key}");
    }
    internal void SetCells(ShellCellHandle[] cells)
    {
        _cells = cells;
        var keys = cells.Select(c => c.Key).ToHashSet();
        foreach (var key in _refs.Keys.Where(k => !keys.Contains(k)).ToArray()) _refs.Remove(key);
    }
    internal ElementRef RefFor(string key)
    {
        if (!_refs.TryGetValue(key, out var reference)) _refs[key] = reference = new();
        return reference;
    }
    internal TrayLayoutSnapshot? Measure(long version, TaskbarTrayPlacement.Surface surface, double windowScale)
    {
        var widths = new List<(string Key, double Width)>();
        foreach (var cell in _cells)
        {
            // DesiredSize includes the host Border's margins; no double counting.
            if (!_refs.TryGetValue(cell.Key, out var reference) || reference.Current is not { } control)
                return null;
            double width = control.DesiredSize.Width;
            if (!double.IsFinite(width) || width < 0) return null;
            widths.Add((cell.Key, width));
        }
        double total = widths.Sum(c => c.Width);
        var bounds = surface.Place(total);
        var cells = ImmutableArray.CreateBuilder<TrayCellLayout>(widths.Count);
        double left = bounds.Left;
        foreach (var (key, width) in widths)
        {
            double right = left + width * surface.Scale;
            cells.Add(new(key, width, new TaskbarInterop.RECT
            {
                Left = (int)left, Right = (int)right, Top = bounds.Top, Bottom = bounds.Bottom,
            }));
            left = right;
        }
        return new(version, cells.MoveToImmutable(), total, surface.Scale, windowScale, bounds);
    }
    internal static bool Equivalent(TrayLayoutSnapshot? a, TrayLayoutSnapshot b) =>
        a != null && a.BoundsPx.Equals(b.BoundsPx) && a.Scale == b.Scale && a.WindowScale == b.WindowScale &&
        Math.Abs(a.WidthDip - b.WidthDip) < 0.001 && a.Cells.SequenceEqual(b.Cells);
}

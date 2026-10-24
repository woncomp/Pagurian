using System.Collections.Immutable;
using Microsoft.UI.Reactor.Input;
using Pagurian.Sdk;

namespace Pagurian;

internal sealed record OverflowCellLayout(string Key, double WidthDip, TaskbarInterop.RECT BoundsPx);
internal sealed record OverflowLayoutSnapshot(long Version, ImmutableArray<OverflowCellLayout> Cells,
    double WidthDip, double Scale, TaskbarInterop.RECT BoundsPx);

// Hit-test geometry for one open overflow tray. Cell refs live in the overflow
// window; scroll offsets are subtracted before rects are clipped to the window.
internal sealed class OverflowTrayLayout
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
        Diagnostic?.Invoke($"overflow-mount key={key}");
    }
    internal void Unmounted(string key)
    {
        UnmountCount++;
        TaskbarTrayWindow.BrushUnmounted(key);
        Diagnostic?.Invoke($"overflow-unmount key={key}");
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

    internal OverflowLayoutSnapshot? Measure(long version, TaskbarInterop.RECT windowBounds,
        double viewportWidthDip, double scrollOffsetDip, double windowScale)
    {
        if (viewportWidthDip <= 0 || windowScale <= 0) return null;
        double contentWidth = 0;
        var widths = new List<(ShellCellHandle Cell, double Width, double Left)>();
        foreach (var cell in _cells)
        {
            if (!_refs.TryGetValue(cell.Key, out var reference) || reference.Current is not { } control)
                return null;
            double width = control.DesiredSize.Width;
            if (!double.IsFinite(width) || width < 0) return null;
            widths.Add((cell, width, contentWidth));
            contentWidth += width;
        }

        var cells = ImmutableArray.CreateBuilder<OverflowCellLayout>(widths.Count);
        foreach (var (cell, width, contentLeft) in widths)
        {
            double leftDip = contentLeft - scrollOffsetDip;
            double rightDip = leftDip + width;
            double clippedLeft = Math.Max(0, leftDip);
            double clippedRight = Math.Min(viewportWidthDip, rightDip);
            if (clippedRight <= clippedLeft) continue;
            cells.Add(new(cell.Key, width, new TaskbarInterop.RECT
            {
                Left = (int)Math.Round(windowBounds.Left + clippedLeft * windowScale),
                Right = (int)Math.Round(windowBounds.Left + clippedRight * windowScale),
                Top = windowBounds.Top + (int)Math.Round(TaskbarTrayWindow.WindowInsetYDip * windowScale),
                Bottom = windowBounds.Bottom - (int)Math.Round(TaskbarTrayWindow.WindowInsetYDip * windowScale),
            }));
        }
        return new(version, cells.MoveToImmutable(), contentWidth, windowScale, windowBounds);
    }
}

using System.Collections.Immutable;
using Microsoft.UI.Reactor.Input;
using Pagurian.Sdk;

namespace Pagurian;

internal sealed record TrayCellLayout(string Key, double WidthDip, TaskbarInterop.RECT BoundsPx);
internal sealed record TrayLayoutSnapshot(long Version, ImmutableArray<TrayCellLayout> Cells,
    double WidthDip, double Scale, double WindowScale, TaskbarInterop.RECT BoundsPx,
    TaskbarInterop.RECT? OverflowArrowBoundsPx, TrayPresentationPlan Plan);

// References belong to one window, not to a global polling feedback loop.
internal sealed class TaskbarTrayLayout
{
    private readonly Dictionary<string, ElementRef> _refs = new();
    private readonly TrayPresentationPlanner _presentation;
    private readonly Dictionary<(string Key, bool Simple), int> _modeAttempts = new();
    private ShellCellHandle[] _cells = [];
    private bool? _overflow;

    internal TaskbarTrayLayout()
    {
        _presentation = new(TaskbarTrayWindow.OverflowArrowWidthDip);
    }
    internal int MountCount { get; private set; }
    internal int UnmountCount { get; private set; }
    internal event Action<string>? Diagnostic;
    internal event Action? PresentationChanged;
    internal bool Overflow => _overflow == true;
    internal TrayPresentationPlan? PendingPlan { get; private set; }
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
        foreach (var attempt in _modeAttempts.Keys.Where(k => !keys.Contains(k.Key)).ToArray())
            _modeAttempts.Remove(attempt);
    }
    internal ElementRef RefFor(string key)
    {
        if (!_refs.TryGetValue(key, out var reference)) _refs[key] = reference = new();
        return reference;
    }
    internal void ApplyRequests(TrayPresentationPlan plan)
    {
        foreach (var request in plan.Requests)
        {
            var cell = _cells.FirstOrDefault(candidate => candidate.Key == request.Key);
            if (cell?.Props.Model is not IShellCellSimpleMode mode) continue;
            // A stale plan can predate a model status change. Never push Simple
            // into a cell that has become ineligible; the next measurement asks
            // it back to Normal instead of marking the mode broken.
            if (request.Simple && cell.Props.Model is IShellCellSimpleModePolicy policy &&
                !policy.CanUseSimpleMode)
                continue;
            var attemptKey = (request.Key, request.Simple);
            try
            {
                if (mode.IsSimpleMode == request.Simple)
                {
                    _modeAttempts.Remove(attemptKey);
                    continue;
                }
                mode.SetSimpleMode(request.Simple);
                if (mode.IsSimpleMode != request.Simple)
                {
                    var attempts = _modeAttempts.GetValueOrDefault(attemptKey) + 1;
                    _modeAttempts[attemptKey] = attempts;
                    if (attempts >= 4)
                    {
                        if (request.Simple) _presentation.MarkSimpleUnsupported(request.Key);
                        else _presentation.MarkModeStuck(request.Key);
                        _modeAttempts.Remove(attemptKey);
                        Diagnostic?.Invoke($"simple-mode request stalled key={request.Key} simple={request.Simple}");
                    }
                }
                else _modeAttempts.Remove(attemptKey);
            }
            catch (Exception ex)
            {
                _presentation.MarkSimpleUnsupported(request.Key);
                _modeAttempts.Remove(attemptKey);
                Diagnostic?.Invoke($"simple-mode request failed key={request.Key}: {ex.Message}");
            }
        }
    }
    internal TrayLayoutSnapshot? Measure(long version, TaskbarTrayPlacement.Surface surface, double windowScale)
    {
        var widths = new List<(ShellCellHandle Cell, double Width)>();
        foreach (var cell in _cells)
        {
            // DesiredSize includes the host Border's margins; no double counting.
            if (!_refs.TryGetValue(cell.Key, out var reference) || reference.Current is not { } control)
                return null;
            double width = control.DesiredSize.Width;
            if (!double.IsFinite(width) || width < 0) return null;
            widths.Add((cell, width));
        }
        var plan = _presentation.Measure(widths, surface.AvailableWidthDip);
        if (plan.Pending)
        {
            PendingPlan = plan;
            return null;
        }
        PendingPlan = null;
        if (_overflow != plan.Overflow)
        {
            _overflow = plan.Overflow;
            PresentationChanged?.Invoke();
        }

        double total = plan.MainWidthDip;
        var visible = plan.MainKeys.ToHashSet();
        var bounds = surface.Place(total);
        var cells = ImmutableArray.CreateBuilder<TrayCellLayout>(widths.Count(c => visible.Contains(c.Cell.Key)));
        double left = bounds.Left;
        foreach (var (cell, width) in widths)
        {
            if (!visible.Contains(cell.Key)) continue;
            double right = left + width * surface.Scale;
            cells.Add(new(cell.Key, width, new TaskbarInterop.RECT
            {
                Left = (int)left, Right = (int)right, Top = bounds.Top, Bottom = bounds.Bottom,
            }));
            left = right;
        }

        TaskbarInterop.RECT? arrow = null;
        if (plan.HasArrow)
        {
            var arrowWidth = plan.ArrowWidthDip * surface.Scale;
            arrow = new TaskbarInterop.RECT
            {
                Left = surface.Edge == TrayEdge.Right ? (int)Math.Round(bounds.Right - arrowWidth) : bounds.Left,
                Right = surface.Edge == TrayEdge.Right ? bounds.Right : (int)Math.Round(bounds.Left + arrowWidth),
                Top = bounds.Top,
                Bottom = bounds.Bottom,
            };
        }
        return new(version, cells.MoveToImmutable(), total, surface.Scale, windowScale, bounds,
            arrow, plan);
    }
    internal static bool Equivalent(TrayLayoutSnapshot? a, TrayLayoutSnapshot b) =>
        a != null && a.BoundsPx.Equals(b.BoundsPx) && a.Scale == b.Scale && a.WindowScale == b.WindowScale &&
        Math.Abs(a.WidthDip - b.WidthDip) < 0.001 && a.Cells.SequenceEqual(b.Cells) &&
        a.OverflowArrowBoundsPx.Equals(b.OverflowArrowBoundsPx) && a.Plan.Equals(b.Plan);
}

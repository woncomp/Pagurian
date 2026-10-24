using System.Collections.Immutable;
using Pagurian.Sdk;

namespace Pagurian;

// Per-surface presentation decision: how much room is available, which cells
// need Simple mode, and where the Normal/Overflow boundary is. The planner is
// pure over measured widths; the session applies mode requests between renders
// so module code never runs inside XAML measure/arrange.
internal sealed record TrayModeRequest(string Key, bool Simple);

internal sealed record TrayPresentationPlan(
    bool Pending,
    bool Overflow,
    ImmutableArray<string> MainKeys,
    ImmutableArray<ShellCellHandle> OverflowCells,
    double MainWidthDip,
    double CapacityDip,
    double ArrowWidthDip,
    ImmutableArray<TrayModeRequest> Requests)
{
    internal bool HasArrow => Overflow && CapacityDip >= ArrowWidthDip && MainWidthDip > 0;
}

internal sealed class TrayPresentationPlanner
{
    private sealed class CellWidths
    {
        internal double? Normal;
        internal double? Simple;
        internal bool SimpleSupported;
        internal bool ModeStuck;
    }

    private sealed class CellState(ShellCellHandle Cell, double ActualWidth,
        bool SimpleSupported, bool Simple, bool SimpleKnown,
        double NormalWidth, double SimpleWidth)
    {
        internal ShellCellHandle Cell { get; } = Cell;
        internal string Key { get; } = Cell.Key;
        internal double ActualWidth { get; } = ActualWidth;
        internal bool SimpleSupported { get; } = SimpleSupported;
        internal bool Simple { get; } = Simple;
        internal bool SimpleKnown { get; } = SimpleKnown;
        internal double NormalWidth { get; } = NormalWidth;
        internal double SimpleWidth { get; } = SimpleWidth;
    }

    private readonly double _arrowWidthDip;
    private readonly Dictionary<string, CellWidths> _widths = new();

    internal TrayPresentationPlanner(double arrowWidthDip) => _arrowWidthDip = arrowWidthDip;

    internal TrayPresentationPlan Measure(
        IReadOnlyList<(ShellCellHandle Cell, double Width)> measured, double capacityDip)
    {
        var states = new List<CellState>(measured.Count);
        var seen = new HashSet<string>();
        foreach (var (cell, width) in measured)
        {
            seen.Add(cell.Key);
            states.Add(State(cell, width));
        }
        foreach (var stale in _widths.Keys.Where(key => !seen.Contains(key)).ToArray())
            _widths.Remove(stale);

        if (states.Count == 0)
            return Plan(false, [], [], capacityDip);

        var normalTotal = states.Sum(state => Width(state, simple: false));
        if (normalTotal <= capacityDip + 0.001)
            return Plan(false, states, [], capacityDip);

        // Simplify one anchored cell at a time. An unknown Simple width yields a
        // request and the next render/measurement; nothing is published until
        // the mode sequence settles.
        var chosen = new HashSet<string>();
        double total = normalTotal;
        foreach (var state in states)
        {
            if (total <= capacityDip + 0.001) break;
            if (!state.SimpleSupported) continue;
            if (!state.Simple && !state.SimpleKnown)
            {
                chosen.Add(state.Key);
                return Plan(false, states, [], capacityDip, chosen.ToArray());
            }
            chosen.Add(state.Key);
            total += Width(state, simple: true) - Width(state, simple: false);
        }
        if (total <= capacityDip + 0.001)
            return Plan(false, states, [], capacityDip, chosen.ToArray());

        // Every possible Simple cell has been tried. Fill complete cells from
        // the anchor; the first non-fitting cell and everything after it move
        // to overflow (in Normal mode).
        var main = new List<CellState>();
        var overflow = new List<CellState>();
        double remaining = capacityDip - _arrowWidthDip;
        bool stopped = false;
        foreach (var state in states)
        {
            var width = Width(state, simple: true);
            if (!stopped && width <= remaining + 0.001)
            {
                main.Add(state);
                remaining -= width;
            }
            else
            {
                stopped = true;
                overflow.Add(state);
            }
        }
        return Plan(true, main, overflow, capacityDip, main.Select(state => state.Key).ToArray());
    }

    internal void MarkSimpleUnsupported(string key)
    {
        if (_widths.TryGetValue(key, out var widths))
        {
            widths.SimpleSupported = false;
            widths.ModeStuck = true;
            widths.Simple = null;
        }
    }

    internal void MarkModeStuck(string key)
    {
        if (!_widths.TryGetValue(key, out var widths)) return;
        widths.SimpleSupported = false;
        widths.ModeStuck = true;
        // Stop retrying a broken Normal restore; use the last Simple width so
        // planning remains conservative but bounded.
        if (widths.Normal == null && widths.Simple is { } simple) widths.Normal = simple;
    }

    private CellState State(ShellCellHandle cell, double width)
    {
        if (!_widths.TryGetValue(cell.Key, out var widths))
            _widths[cell.Key] = widths = new();

        bool simple = false;
        bool supported = false;
        if (cell.Props.Model is IShellCellSimpleMode mode && !widths.ModeStuck)
        {
            try
            {
                simple = mode.IsSimpleMode;
                supported = true;
            }
            catch
            {
                widths.ModeStuck = true;
                widths.SimpleSupported = false;
                widths.Simple = null;
            }
        }
        widths.SimpleSupported = supported;

        if (simple) widths.Simple = width;
        else widths.Normal = width;

        return new(cell, width, supported, simple, widths.Simple.HasValue,
            widths.Normal ?? width, widths.Simple ?? width);
    }

    private static double Width(CellState state, bool simple) =>
        simple && state.SimpleSupported ? state.SimpleWidth : state.NormalWidth;

    private TrayPresentationPlan Plan(bool overflow, IReadOnlyList<CellState> main,
        IReadOnlyList<CellState> overflowCells, double capacityDip, IReadOnlyCollection<string>? simpleKeys = null)
    {
        simpleKeys ??= overflow
            ? new HashSet<string>(main.Select(state => state.Key))
            : new HashSet<string>();
        var requests = new List<TrayModeRequest>();
        foreach (var state in main.Concat(overflowCells))
        {
            bool simple = state.SimpleSupported && simpleKeys.Contains(state.Key);
            if (state.Simple != simple)
                requests.Add(new(state.Key, simple));
        }

        double mainWidth = overflow ? _arrowWidthDip : 0;
        foreach (var state in main)
            mainWidth += Width(state, simpleKeys.Contains(state.Key));

        return new(
            Pending: requests.Count > 0,
            Overflow: overflow,
            MainKeys: [.. main.Select(state => state.Key)],
            OverflowCells: [.. overflowCells.Select(state => state.Cell)],
            MainWidthDip: mainWidth,
            CapacityDip: capacityDip,
            ArrowWidthDip: overflow ? _arrowWidthDip : 0,
            Requests: [.. requests]);
    }
}

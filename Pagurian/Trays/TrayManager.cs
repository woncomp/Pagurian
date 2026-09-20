using Microsoft.UI.Reactor;
using Pagurian.Sdk;

namespace Pagurian;

// The tray registry: owns the configured logical trays (config order), the
// global cell index, `post` routing, and the binding engine that maps each
// tray onto a live presentation surface.
//
// Layers stay one-directional: DisplayTopology reports which displays exist;
// TrayManager binds logical trays to surfaces; TraySurface renders whatever
// the binding assigns to it. A tray whose display is disconnected keeps
// running (shells stay up, messages keep routing) while its cells are
// rendered on the surface the fallback chain selects:
//   1. the taskbar-bearing display with the closest aspect ratio to the
//      missing display's recorded geometry (monitors.json);
//   2. otherwise a taskbar-bearing display no configured tray owns;
//   3. otherwise the primary display.
// The chain is recomputed on every topology/config change, so a reconnected
// display gets its tray back automatically.
static class TrayManager
{
    // |ln(a/b)| tolerance for the aspect-ratio fallback step (~16%).
    private const double AspectTolerance = 0.15;

    // Where trays land when no topology is known at all (fixtures, transient
    // enumeration failure): the implicit primary-left surface.
    internal static readonly SurfaceKey DefaultSurfaceKey = new(TrayId.PrimaryMonitorKey, TrayEdge.Left);

    private static readonly List<ShellTray> _trays = new();
    private static readonly Dictionary<TrayId, ShellTray> _trayById = new();
    private static readonly Dictionary<string, ShellCellHandle> _cellsByKey = new();
    private static ShellCellHandle[] _cells = [];
    private static readonly Dictionary<TrayId, SurfaceKey> _binding = new();
    private static readonly Dictionary<SurfaceKey, ShellCellHandle[]> _surfaceCells = new();
    private static readonly Dictionary<SurfaceKey, IThemeService> _surfaceThemes = new();
    private static IThemeService? _unboundTheme;
    private static IReadOnlyList<DisplayInfo> _displays = [];
    private static int _batchDepth;
    private static bool _publishQueued;
    private static long _publicationTicket;

    // Raised on the UI thread whenever the effective per-surface cell sets
    // change — cell edits, config reconciliation, or rebinding.
    public static event Action? Changed;

    // History lookup for the aspect-ratio fallback step, wired by the
    // controller from DisplayTopology. Null in fixtures skips that step.
    internal static Func<string, RecordedMonitor?>? MonitorHistory { get; set; }

    public static IReadOnlyList<Shell> Shells => _trays.SelectMany(t => t.Shells).ToArray();

    public static IReadOnlyList<ShellTray> Trays => _trays;

    public static ShellCellHandle? FindCell(string key) =>
        _cellsByKey.TryGetValue(key, out var h) ? h : null;

    // The cells a surface renders: its own tray first, then any trays the
    // fallback chain bound to it, all in config order.
    public static IReadOnlyList<ShellCellHandle> CellsForSurface(SurfaceKey key) =>
        _surfaceCells.TryGetValue(key, out var cells) ? cells : [];

    // Where a tray currently renders (test/diagnostic seam).
    internal static SurfaceKey BindingOf(TrayId tray) =>
        _binding.TryGetValue(tray, out var key) ? key : DefaultSurfaceKey;

    private static IThemeService UnboundTheme => _unboundTheme ??= new ThemeService();

    private static IThemeService ThemeFor(SurfaceKey key) =>
        _surfaceThemes.TryGetValue(key, out var theme) ? theme : UnboundTheme;

    // First load at startup; same pipeline as ApplyConfig (the registry is
    // still empty, so everything is an add).
    public static void LoadFromConfig(IReadOnlyList<TrayConfig.TrayGroup> groups) =>
        ApplyConfig(groups);

    // Reconciles the live trays with the given groups: trays absent from the
    // list are shut down, new trays are created, and each surviving tray
    // reconciles its own entries — all without restarting shell instances
    // that stay on the same tray. Ends with one Publish.
    public static void ApplyConfig(IReadOnlyList<TrayConfig.TrayGroup> groups)
    {
        _batchDepth++;
        try
        {
            var wanted = groups.Select(g => g.Id).ToHashSet();
            foreach (var tray in _trays.Where(t => !wanted.Contains(t.Id)).ToList())
            {
                tray.ShutdownAll();
                _trayById.Remove(tray.Id);
                _binding.Remove(tray.Id);
                _trays.Remove(tray);
                PagurianLog.Host($"tray {tray.Id}: removed");
            }

            var ordered = new List<ShellTray>(groups.Count);
            foreach (var group in groups)
            {
                if (!_trayById.TryGetValue(group.Id, out var tray))
                {
                    tray = new ShellTray(group.Id, ThemeFor(BindingOf(group.Id)));
                    tray.CellsChanged += () => QueuePublication();
                    _trayById[group.Id] = tray;
                    PagurianLog.Host($"tray {group.Id}: created");
                }
                tray.ApplyEntries(group.Entries);
                ordered.Add(tray);
            }
            _trays.Clear();
            _trays.AddRange(ordered);
        }
        finally
        {
            if (--_batchDepth == 0) Publish();
        }
    }

    // New live-display set (from DisplayTopology). Rebinds and republishes.
    public static void SetTopology(IReadOnlyList<DisplayInfo> displays)
    {
        _displays = displays;
        if (_batchDepth == 0)
            Publish();
    }

    // Surfaces register their sampled theme so trays bound to them (and the
    // shells inside) follow the taskbar they actually render on.
    internal static void RegisterSurfaceTheme(SurfaceKey key, IThemeService theme)
    {
        _surfaceThemes[key] = theme;
        foreach (var tray in _trays)
            if (BindingOf(tray.Id) == key)
                tray.SetTheme(theme);
    }

    internal static void UnregisterSurfaceTheme(SurfaceKey key) =>
        _surfaceThemes.Remove(key);

    public static void RouteMessage(string instanceId, ShellMessage message)
    {
        var shell = _trays
            .Select(tray => tray.FindShell(instanceId))
            .FirstOrDefault(found => found != null);
        if (shell == null)
        {
            PagurianLog.Host(
                $"post: no shell with id {instanceId}; dropped {message.Command}");
            return;
        }
        try
        {
            shell.OnMessage(message);
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"post: {instanceId} failed on {message.Command}", ex);
        }
    }

    public static void ShutdownAll()
    {
        _batchDepth++;
        try
        {
            foreach (var tray in _trays)
                tray.ShutdownAll();
            _trays.Clear();
            _trayById.Clear();
        }
        finally { if (--_batchDepth == 0) Publish(); }
    }

    private static void QueuePublication()
    {
        // Invalidate removed interaction targets immediately, even when the
        // visual list publication is coalesced until the end of this UI turn.
        var liveKeys = _trays
            .SelectMany(t => t.Shells)
            .SelectMany(s => s.Cells)
            .Select(c => c.Key)
            .ToHashSet();
        foreach (var key in _cellsByKey.Keys.Where(k => !liveKeys.Contains(k)).ToArray())
            _cellsByKey.Remove(key);
        if (_batchDepth > 0 || _publishQueued) return;
        _publishQueued = true;
        long ticket = ++_publicationTicket;
        if (ReactorApp.UIDispatcher?.TryEnqueue(() =>
        {
            if (ticket == _publicationTicket && _batchDepth == 0) Publish();
        }) != true) Publish();
    }

    private static void Publish()
    {
        _publishQueued = false;
        var cellsChanged = RebuildCellIndex();
        var compositionChanged = Rebind();
        if (cellsChanged || compositionChanged)
            Changed?.Invoke();
    }

    private static bool RebuildCellIndex()
    {
        foreach (var tray in _trays)
            tray.RebuildCells();
        var next = _trays.SelectMany(tray => tray.Cells).ToArray();
        if (_cells.SequenceEqual(next))
            return false;
        _cells = next;
        _cellsByKey.Clear();
        foreach (var cell in next)
            _cellsByKey.Add(cell.Key, cell);
        return true;
    }

    // Recomputes tray→surface bindings, pushes surface themes into the trays,
    // and recomposes the per-surface cell lists. Returns true when any
    // surface-visible state changed.
    private static bool Rebind()
    {
        var changed = _binding.Count != _trays.Count;
        var next = new Dictionary<TrayId, SurfaceKey>(_trays.Count);
        foreach (var tray in _trays)
        {
            var key = ResolveSurface(tray.Id, out var reason);
            next[tray.Id] = key;
            if (!_binding.TryGetValue(tray.Id, out var previous) || previous != key)
            {
                changed = true;
                PagurianLog.Host($"tray {tray.Id}: bound to {key} ({reason})");
            }
        }
        _binding.Clear();
        foreach (var (id, key) in next)
            _binding[id] = key;

        foreach (var tray in _trays)
            tray.SetTheme(ThemeFor(_binding[tray.Id]));

        var nextCells = new Dictionary<SurfaceKey, ShellCellHandle[]>();
        foreach (var tray in _trays)
        {
            if (tray.Cells.Count == 0)
                continue;
            var key = _binding[tray.Id];
            nextCells.TryGetValue(key, out var existing);
            nextCells[key] = existing == null ? tray.Cells.ToArray() : [.. existing, .. tray.Cells];
        }
        if (_surfaceCells.Count != nextCells.Count ||
            nextCells.Any(kv => !_surfaceCells.TryGetValue(kv.Key, out var old) || !old.SequenceEqual(kv.Value)))
            changed = true;
        _surfaceCells.Clear();
        foreach (var kv in nextCells)
            _surfaceCells[kv.Key] = kv.Value;
        return changed;
    }

    private static SurfaceKey ResolveSurface(TrayId tray, out string reason)
    {
        var candidates = _displays.Where(d => d.HasTaskbar).ToArray();
        if (candidates.Length == 0)
        {
            reason = "no topology";
            return DefaultSurfaceKey;
        }

        var exact = tray.IsPrimaryAlias
            ? candidates.FirstOrDefault(d => d.IsPrimary)
            : candidates.FirstOrDefault(d => d.IdentityKey == tray.MonitorKey);
        if (exact != null)
        {
            reason = tray.IsPrimaryAlias ? "primary alias" : "configured display";
            return LeftSurface(exact, tray, ref reason);
        }

        // 1. Closest aspect ratio to the missing display's recorded geometry.
        if (MonitorHistory?.Invoke(tray.MonitorKey) is { Width: > 0, Height: > 0 } recorded)
        {
            double wanted = Math.Log((double)recorded.Width / recorded.Height);
            var best = candidates
                .Select(d => (Display: d, Delta: Math.Abs(Math.Log((double)d.Rect.Width / d.Rect.Height) - wanted)))
                .MinBy(x => x.Delta);
            if (best.Delta <= AspectTolerance)
            {
                reason = $"aspect-ratio match for absent {tray.MonitorKey} ({recorded.Width}x{recorded.Height})";
                return LeftSurface(best.Display, tray, ref reason);
            }
        }

        // 2. A display no configured tray owns.
        var empty = candidates.FirstOrDefault(d => !_trays.Any(t => Owns(t.Id, d)));
        if (empty != null)
        {
            reason = $"first unoccupied display for absent {tray.MonitorKey}";
            return LeftSurface(empty, tray, ref reason);
        }

        // 3. The primary display.
        var primary = candidates.FirstOrDefault(d => d.IsPrimary) ?? candidates[0];
        reason = $"primary fallback for absent {tray.MonitorKey}";
        return LeftSurface(primary, tray, ref reason);
    }

    // A tray owns a display when the config points at it directly — by
    // identity key, or via the primary alias for the primary display.
    private static bool Owns(TrayId tray, DisplayInfo display) =>
        tray.IsPrimaryAlias ? display.IsPrimary : tray.MonitorKey == display.IdentityKey;

    // Until right-edge surfaces are materialized (see TaskbarTrayPlacement),
    // every tray renders on its display's left surface. The configured edge
    // stays on TrayId so configs round-trip untouched.
    private static SurfaceKey LeftSurface(DisplayInfo display, TrayId tray, ref string reason)
    {
        if (tray.Edge != TrayEdge.Left)
            reason += $"; {TrayId.EdgeName(tray.Edge)} edge is not yet supported, using left";
        return new SurfaceKey(display.IdentityKey, TrayEdge.Left);
    }
}

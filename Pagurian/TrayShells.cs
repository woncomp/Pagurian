using Pagurian.Sdk;

namespace Pagurian;

// The tray's shell registry: owns the ordered list of configured shells
// (config order = tray order) and the flattened cell sequence the window
// renders and the controller hit-tests.
//
// Shells attach/detach cells through Shell.AddCell/RemoveCell, which call
// back here via the internal channel; every such change raises Changed so
// the tray window re-renders as a whole (no fine-grained diff — cell counts
// are tiny).
static class TrayShells
{
    private static readonly List<Shell> _shells = new();
    private static readonly List<ShellCellHandle> _cells = new();
    private static readonly Dictionary<string, ShellCellHandle> _cellsByKey = new();

    // Raised on the UI thread whenever the flattened cell set changes.
    public static event Action? Changed;

    public static IReadOnlyList<ShellCellHandle> Cells => _cells;

    // The live shell instances in tray order (the Shell editor diffs its
    // draft against this list via ApplyConfig).
    public static IReadOnlyList<Shell> Shells => _shells;

    public static ShellCellHandle? FindCell(string key) =>
        _cellsByKey.TryGetValue(key, out var h) ? h : null;

    // First load at startup; same pipeline as ApplyConfig (the registry is
    // still empty, so everything is an add).
    public static void LoadFromConfig(IReadOnlyList<TrayConfig.Entry> entries) =>
        ApplyConfig(entries);

    // Reconciles the live shells with the given entries: instances whose id
    // is absent are shut down and removed, new entries are created and
    // started, and the survivors take the entries' order — all without
    // restarting instances that are already live. Ends with one RebuildCells.
    public static void ApplyConfig(IReadOnlyList<TrayConfig.Entry> entries)
    {
        var wanted = new HashSet<string>(entries.Select(e => e.Id));
        foreach (var shell in _shells.Where(s => !wanted.Contains(s.InstanceId)).ToList())
        {
            ShutdownOne(shell);
            _shells.Remove(shell);
            PagurianLog.Host($"tray: shell {shell.GetType().FullName} stopped (#{shell.InstanceId})");
        }

        var ordered = new List<Shell>(entries.Count);
        foreach (var entry in entries)
        {
            var live = _shells.FirstOrDefault(s => s.InstanceId == entry.Id);
            // An id that maps to a different kind is a config rewrite, not a
            // keep: restart it under the new kind.
            if (live != null && live.GetType().FullName == entry.ShellType)
            {
                if (!SettingsEqual(live.Settings, entry.Settings))
                {
                    live.Settings = entry.Settings;
                    try
                    {
                        live.OnSettingsChanged();
                        PagurianLog.Host(
                            $"tray: shell {entry.ShellType} settings updated (#{entry.Id})");
                    }
                    catch (Exception ex)
                    {
                        PagurianLog.HostError(
                            $"tray: {entry.ShellType} OnSettingsChanged failed", ex);
                    }
                }
                ordered.Add(live);
                continue;
            }
            if (live != null)
            {
                ShutdownOne(live);
                _shells.Remove(live);
            }
            var started = StartEntry(entry);
            if (started != null)
                ordered.Add(started);
        }
        _shells.Clear();
        _shells.AddRange(ordered);
        RebuildCells();
    }

    private static bool SettingsEqual(
        System.Text.Json.JsonElement? left,
        System.Text.Json.JsonElement? right)
    {
        if (left == null || right == null)
            return left == null && right == null;
        return System.Text.Json.JsonElement.DeepEquals(left.Value, right.Value);
    }

    // Instantiation pipeline for one config entry: kind lookup -> factory ->
    // inject context -> Startup. Failures are logged and yield null (kind
    // missing / constructor threw) or a registered-but-inert shell (Startup
    // threw), matching the historical load behavior.
    private static Shell? StartEntry(TrayConfig.Entry entry)
    {
        if (!ModuleLoader.TryGetKind(entry.ShellType, out var kind))
        {
            PagurianLog.HostError(
                $"tray: no loaded module offers shell kind {entry.ShellType}; skipped");
            return null;
        }

        Shell shell;
        try
        {
            shell = kind.CreateShell();
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"tray: failed to create {entry.ShellType}", ex);
            return null;
        }

        shell.InstanceId = entry.Id;
        shell.Settings = entry.Settings;
        shell.Theme = ThemeService.Instance;
        shell.Log = Logger.For($"{entry.ShellType}#{entry.Id}");
        shell.Channel = new Channel();

        try
        {
            shell.Startup();
            PagurianLog.Host($"tray: shell {entry.ShellType} started as #{entry.Id}");
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"tray: {entry.ShellType} Startup failed", ex);
        }
        return shell;
    }

    public static void RouteMessage(string instanceId, ShellMessage message)
    {
        var shell = _shells.FirstOrDefault(s => s.InstanceId == instanceId);
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
        foreach (var shell in _shells)
            ShutdownOne(shell);
        _shells.Clear();
        RebuildCells();
    }

    private static void ShutdownOne(Shell shell)
    {
        try
        {
            shell.Shutdown();
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"tray: {shell.GetType().FullName} Shutdown failed", ex);
        }
    }

    private static void RebuildCells()
    {
        _cells.Clear();
        _cellsByKey.Clear();
        foreach (var shell in _shells)
        {
            foreach (var cell in shell.Cells)
            {
                _cells.Add(cell);
                _cellsByKey[cell.Key] = cell;
            }
        }
        Changed?.Invoke();
    }

    private sealed class Channel : IShellHostChannel
    {
        public void NotifyCellsChanged() => RebuildCells();
    }
}

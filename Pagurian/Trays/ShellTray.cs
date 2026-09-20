using Pagurian.Sdk;

namespace Pagurian;

// One logical tray: the config-owned, ordered set of shell instances belonging
// to a display/edge pair. A tray exists — and its shells keep running and
// receiving `post` messages — regardless of whether its display is currently
// connected; presentation is a separate concern (TrayManager binds trays to
// live surfaces, and the binding may point at another display while the
// configured one is absent).
//
// Shells attach/detach cells through Shell.AddCell/RemoveCell, which call back
// through the internal channel; TrayManager coalesces those into publishes.
internal sealed class ShellTray
{
    private readonly List<Shell> _shells = new();

    internal ShellTray(TrayId id, IThemeService theme)
    {
        Id = id;
        Theme = theme;
    }

    internal TrayId Id { get; }

    // The theme of the surface this tray is currently bound to. Re-pointed by
    // TrayManager when the binding changes; live shells are updated in place
    // (their cells re-read Theme when they mount on the new surface).
    internal IThemeService Theme { get; private set; }

    internal IReadOnlyList<Shell> Shells => _shells;

    internal IReadOnlyList<ShellCellHandle> Cells { get; private set; } = [];

    internal event Action? CellsChanged;

    internal void SetTheme(IThemeService theme)
    {
        if (ReferenceEquals(Theme, theme))
            return;
        Theme = theme;
        foreach (var shell in _shells)
            shell.Theme = theme;
    }

    internal Shell? FindShell(string instanceId) =>
        _shells.FirstOrDefault(s => s.InstanceId == instanceId);

    // Reconciles the live shells with the given entries: instances whose id
    // is absent are shut down and removed, new entries are created and
    // started, and the survivors take the entries' order — all without
    // restarting instances that are already live. Cell list publication is
    // deferred to the caller's Publish.
    internal void ApplyEntries(IReadOnlyList<TrayConfig.Entry> entries)
    {
        var wanted = new HashSet<string>(entries.Select(e => e.Id));
        foreach (var shell in _shells.Where(s => !wanted.Contains(s.InstanceId)).ToList())
        {
            ShutdownOne(shell);
            _shells.Remove(shell);
            PagurianLog.Host($"tray {Id}: shell {shell.GetType().FullName} stopped (#{shell.InstanceId})");
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
                            $"tray {Id}: shell {entry.ShellType} settings updated (#{entry.Id})");
                    }
                    catch (Exception ex)
                    {
                        PagurianLog.HostError(
                            $"tray {Id}: {entry.ShellType} OnSettingsChanged failed", ex);
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
    private Shell? StartEntry(TrayConfig.Entry entry)
    {
        if (!ModuleLoader.TryGetKind(entry.ShellType, out var kind))
        {
            PagurianLog.HostError(
                $"tray {Id}: no loaded module offers shell kind {entry.ShellType}; skipped");
            return null;
        }

        Shell shell;
        try
        {
            shell = kind.CreateShell();
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"tray {Id}: failed to create {entry.ShellType}", ex);
            return null;
        }

        shell.InstanceId = entry.Id;
        shell.Settings = entry.Settings;
        shell.Theme = Theme;
        shell.Log = Logger.For($"{entry.ShellType}#{entry.Id}");
        shell.Channel = new Channel(this);

        try
        {
            shell.Startup();
            PagurianLog.Host($"tray {Id}: shell {entry.ShellType} started as #{entry.Id}");
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"tray {Id}: {entry.ShellType} Startup failed", ex);
        }
        return shell;
    }

    internal void ShutdownAll()
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

    // Recomputes the flattened cell list; returns true when it changed.
    internal bool RebuildCells()
    {
        var next = _shells.SelectMany(shell => shell.Cells).ToArray();
        if (Cells.SequenceEqual(next))
            return false;
        Cells = next;
        return true;
    }

    internal void NotifyCellsChanged() => CellsChanged?.Invoke();

    private sealed class Channel(ShellTray tray) : IShellHostChannel
    {
        public void NotifyCellsChanged() => tray.NotifyCellsChanged();
    }
}

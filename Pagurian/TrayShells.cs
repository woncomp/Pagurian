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

    public static ShellCellHandle? FindCell(string key) =>
        _cellsByKey.TryGetValue(key, out var h) ? h : null;

    // Instantiation pipeline for one config entry: kind lookup -> factory ->
    // inject context -> register -> Startup. Any failure is logged and the
    // entry skipped; the tray keeps loading the rest.
    public static void LoadFromConfig(IReadOnlyList<TrayConfig.Entry> entries)
    {
        foreach (var entry in entries)
        {
            if (!ModuleLoader.TryGetKind(entry.ShellType, out var kind))
            {
                PagurianLog.HostError(
                    $"tray: no loaded module offers shell kind {entry.ShellType}; skipped");
                continue;
            }

            Shell shell;
            try
            {
                shell = kind.CreateShell();
            }
            catch (Exception ex)
            {
                PagurianLog.HostError($"tray: failed to create {entry.ShellType}", ex);
                continue;
            }

            shell.InstanceId = entry.Id;
            shell.Settings = entry.Settings;
            shell.Theme = ThemeService.Instance;
            shell.Log = Logger.For($"{entry.ShellType}#{entry.Id}");
            shell.Channel = new Channel();
            _shells.Add(shell);

            try
            {
                shell.Startup();
                PagurianLog.Host($"tray: shell {entry.ShellType} started as #{entry.Id}");
            }
            catch (Exception ex)
            {
                PagurianLog.HostError($"tray: {entry.ShellType} Startup failed", ex);
            }
        }
        RebuildCells();
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
        _shells.Clear();
        RebuildCells();
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

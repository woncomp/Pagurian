using System.Text.Json;

namespace Pagurian.Sdk;

// A configured shell instance: the unit the tray directly manages, the target
// of "Pagurian.exe post {id}" messages, and the container of ShellCells.
// Shells are plain objects (not Reactor components); rendering happens in the
// cells. A shell with zero cells occupies no tray space — dynamic shells
// (e.g. Copilot) stay empty until events arrive and then AddCell/RemoveCell.
public abstract class Shell
{
    private readonly List<ShellCellHandle> _cells = new();

    // Persistent 4-digit id from config.json, injected by the host before
    // Startup. This is the id `post` addresses.
    public string InstanceId { get; internal set; } = "";

    // The config entry's optional "settings" object, passed through verbatim
    // (the host never parses it; the schema belongs to the shell kind).
    public JsonElement? Settings { get; internal set; }

    public IThemeService Theme { get; internal set; } = null!;

    public Logger Log { get; internal set; } = Logger.For("shell");

    internal IShellHostChannel? Channel { get; set; }

    internal IReadOnlyList<ShellCellHandle> Cells => _cells;

    // Called after the host has injected InstanceId/Settings/Theme/Log.
    // Bring heavy resources online here (samplers, hook files, listeners).
    public virtual void Startup() { }

    // Called when the shell leaves the tray (app quit). Take resources
    // offline; remaining cells are dropped with it.
    public virtual void Shutdown() { }

    // A "Pagurian.exe post {id} <cmd> [args...]" message addressed to this
    // shell. Runs on the UI thread.
    public virtual void OnMessage(ShellMessage message) { }

    // Attaches a cell to this shell. TCell is the Reactor component rendering
    // the cell's content (Reactor creates and pools component instances by
    // type, so per-cell state must live in `model`, not in TCell fields).
    // The delegates capture the model and stay live:
    //  - tooltip: hover-dwell tooltip text (null/empty = no tooltip);
    //  - billboard: factory for the cell's billboard (null = click does
    //    nothing unless onClicked is set);
    //  - onClicked: full custom click behavior; when set it replaces the
    //    default "toggle billboard" behavior entirely.
    protected ShellCellHandle AddCell<TCell>(
        object? model = null,
        Func<string?>? tooltip = null,
        Func<Billboard?>? billboard = null,
        Action? onClicked = null)
        where TCell : ShellCell
    {
        var handle = new ShellCellHandle(
            Guid.NewGuid().ToString("N"),
            typeof(TCell),
            new ShellCellProps(this, model, tooltip, billboard, onClicked));
        _cells.Add(handle);
        Channel?.NotifyCellsChanged();
        return handle;
    }

    protected void RemoveCell(ShellCellHandle cell)
    {
        if (_cells.Remove(cell))
            Channel?.NotifyCellsChanged();
    }
}

// Opaque per-cell registration the tray renders and hit-tests. The Key is a
// host-internal identity (reconciliation key, hover brush cache, billboard
// ownership); it is intentionally NOT addressable via `post`.
public sealed record ShellCellHandle(string Key, Type ViewType, ShellCellProps Props);

namespace Pagurian.Sdk;

// Optional per-cell presentation contract implemented by a cell's MODEL, not by
// the ShellCell component: Reactor creates/pools component instances by type,
// while model state is explicitly shared with the host. A model that does not
// implement this interface supports Normal mode only.
//
// The host calls SetSimpleMode on the UI thread when taskbar capacity changes.
// Implementations must be idempotent, update presentation state synchronously,
// and raise their existing model Changed event so the cell re-renders and the
// tray can re-measure. Simple mode should be a narrower icon/summary
// presentation; the host never interprets model-specific state.
public interface IShellCellSimpleMode
{
    bool IsSimpleMode { get; }

    void SetSimpleMode(bool simple);
}

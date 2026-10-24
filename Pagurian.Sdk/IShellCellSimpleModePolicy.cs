namespace Pagurian.Sdk;

// Optional companion to IShellCellSimpleMode for models that can be Simple
// only in some states, or that want the host to prefer particular cells inside
// one Shell. Implement it on the same model that implements simple mode.
//
// CanUseSimpleMode is read on the UI thread before each mode request. A cell
// that currently reports false is never asked for Simple mode and is restored
// to Normal mode. SimpleModePriority orders eligible cells within their owning
// Shell only; smaller values are simplified first and equal values keep the
// tray's anchored cell order. Shell order, visual order and overflow order are
// unchanged. Both properties are synchronous observations of live model state.
public interface IShellCellSimpleModePolicy
{
    bool CanUseSimpleMode { get; }

    long SimpleModePriority { get; }
}

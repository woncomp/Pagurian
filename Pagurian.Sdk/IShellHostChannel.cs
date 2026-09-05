namespace Pagurian.Sdk;

// Internal channel from a Shell back to the host (tray change notification).
// Internal so modules can neither implement nor call it; the host sees it via
// InternalsVisibleTo.
internal interface IShellHostChannel
{
    void NotifyCellsChanged();
}

using Pagurian.Sdk;

namespace Pagurian.Modules.Hello;

// One World Clock shell instance tracks one city: drag additional instances
// into the tray for more cities. The cell renders that city's time over its
// label; clicking toggles the details billboard (the default OnClicked).
[Shell(DisplayName = "World Clock", ConfigurationView = typeof(WorldClockConfiguration))]
public sealed class WorldClockShell : Shell
{
    // Raised on the UI thread after a saved configuration change updates
    // Settings; the cell and billboard subscribe to re-render live.
    public event Action? Changed;

    public override void Startup() =>
        AddCell<WorldClockCell>(
            tooltip: () => WorldClockCell.Tooltip(this),
            billboard: () => new WorldClockBillboard());

    public override void OnSettingsChanged() => Changed?.Invoke();
}

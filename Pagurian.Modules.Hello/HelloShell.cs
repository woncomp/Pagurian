using Pagurian.Sdk;

namespace Pagurian.Modules.Hello;

// The demo shell: one clock cell whose click toggles the Hello billboard
// (the default OnClicked behavior — no custom click handler needed).
[Shell(DisplayName = "Hello Clock", ConfigurationView = typeof(HelloConfiguration))]
public sealed class HelloShell : Shell
{
    public override void Startup() =>
        AddCell<ClockCell>(billboard: () => new HelloBillboard());
}

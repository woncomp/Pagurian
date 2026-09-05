using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Hello;

// The Hello billboard: a "Hello World" line and a button that shows a Win32
// message box. Window chrome and placement are host-managed; only the
// content and the design size live here.
class HelloBillboard : Billboard
{
    public override double WidthDip => 240;

    public override double HeightDip => 84;

    public override string Title => "Pagurian";

    public override Element Render() =>
        FlexColumn(
            TextBlock("Hello World")
                .FontSize(16),
            Button("Say Hello Again",
                    () => MessageBoxes.Show("Hello World Again!", "Pagurian"))
                .Margin(0, 12, 0, 0))
            .Padding(14);
}

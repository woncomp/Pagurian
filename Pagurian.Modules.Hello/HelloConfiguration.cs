using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Hello;

class HelloConfiguration : ShellConfiguration
{
    public override Element Render()
    {
        var message = HelloSettings.Message(Settings);
        return FlexColumn(
            TextBlock("Message-box text")
                .FontSize(12)
                .SemiBold()
                .Foreground(Theme.TextBrush),
            TextBox(message, value => SetSettings(HelloSettings.Write(value)))
                .AutomationName("Message-box text")
                .AcceptsReturn()
                .TextWrapping(TextWrapping.Wrap)
                .Height(96)
                .Margin(0, 6, 0, 0));
    }
}

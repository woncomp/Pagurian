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
            BodyStrong("Message-box text"),
            TextBox(message, value => SetSettings(HelloSettings.Write(value)))
                .AutomationName("Message-box text")
                .AcceptsReturn()
                .TextWrapping(TextWrapping.Wrap)
                .MinHeight(96)
                .Margin(0, 8, 0, 0));
    }
}

using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Automation.Peers;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;

namespace Pagurian.Modules.Copilot;

internal sealed class CopilotSdkConfiguration : ShellConfiguration
{
    public override Element Render() =>
        FlexColumn(
            BodyStrong("Copilot SDK monitoring")
                .HeadingLevel(AutomationHeadingLevel.Level2),
            Caption("This shell reads only the Copilot SDK's local persisted session API. "
                + "It does not install or consume hooks, resume sessions, or attach to their runtimes.")
                .TextWrapping(Microsoft.UI.Xaml.TextWrapping.WrapWholeWords)
                .Foreground(ReactorTheme.SecondaryText)
                .Margin(0, 8, 0, 0),
            Caption("Active membership: another process holds the SDK session lock. "
                + "Polling: active sessions every 3 seconds; membership every 5 seconds.")
                .TextWrapping(Microsoft.UI.Xaml.TextWrapping.WrapWholeWords)
                .Foreground(ReactorTheme.SecondaryText)
                .Margin(0, 8, 0, 0),
            Caption("Status is inferred from the latest 10 persisted events. "
                + "Blocked is confirmed only after the same evidence survives a fresh 2-second read. "
                + "Unknown means the persisted journal cannot prove the current state.")
                .TextWrapping(Microsoft.UI.Xaml.TextWrapping.WrapWholeWords)
                .Foreground(ReactorTheme.SecondaryText)
                .Margin(0, 8, 0, 0));
}

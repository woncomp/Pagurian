using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Automation.Peers;
using System.Globalization;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;

namespace Pagurian.Modules.Copilot;

internal sealed class CopilotSdkConfiguration : ShellConfiguration
{
    public override Element Render()
    {
        var polling = CopilotSdkPollingSettings.Read(Settings);
        string discovery = polling.DiscoverySeconds.ToString(CultureInfo.InvariantCulture);
        string status = polling.StatusSeconds.ToString(CultureInfo.InvariantCulture);

        return FlexColumn(
            BodyStrong("Copilot SDK monitoring")
                .HeadingLevel(AutomationHeadingLevel.Level2),
            Caption("This shell reads only the Copilot SDK's local persisted session API. "
                + "It does not install or consume hooks, resume sessions, or attach to their runtimes.")
                .TextWrapping(Microsoft.UI.Xaml.TextWrapping.WrapWholeWords)
                .Foreground(ReactorTheme.SecondaryText)
                .Margin(0, 8, 0, 0),
            Caption("Active membership: another process holds the SDK session lock. "
                + $"Polling: active sessions every {status} seconds; membership every {discovery} seconds.")
                .TextWrapping(Microsoft.UI.Xaml.TextWrapping.WrapWholeWords)
                .Foreground(ReactorTheme.SecondaryText)
                .Margin(0, 8, 0, 0),
            BodyStrong("Polling intervals")
                .HeadingLevel(AutomationHeadingLevel.Level2)
                .Margin(0, 16, 0, 0),
            Caption($"Membership scan ({CopilotSdkPollingSettings.MinimumSeconds}-"
                + $"{CopilotSdkPollingSettings.MaximumSeconds} seconds)"),
            TextBox(
                discovery,
                value => SetInterval(value, polling, statusInterval: false),
                placeholderText: "Seconds")
                .AutomationName("Membership scan interval in seconds"),
            Caption($"Status scan ({CopilotSdkPollingSettings.MinimumSeconds}-"
                + $"{CopilotSdkPollingSettings.MaximumSeconds} seconds)")
                .Margin(0, 8, 0, 0),
            TextBox(
                status,
                value => SetInterval(value, polling, statusInterval: true),
                placeholderText: "Seconds")
                .AutomationName("Status scan interval in seconds"),
            Caption("Status is inferred from the latest 10 persisted events. "
                + "Blocked is confirmed only after the same evidence survives a fresh 2-second read. "
                + "Unknown means the persisted journal cannot prove the current state.")
                .TextWrapping(Microsoft.UI.Xaml.TextWrapping.WrapWholeWords)
                .Foreground(ReactorTheme.SecondaryText)
                .Margin(0, 8, 0, 0));
    }

    private void SetInterval(
        string text,
        CopilotSdkPollingSettings current,
        bool statusInterval)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var seconds))
            return;
        var next = statusInterval
            ? current with { StatusSeconds = seconds }
            : current with { DiscoverySeconds = seconds };
        SetSettings(next.Normalize().Write());
    }
}

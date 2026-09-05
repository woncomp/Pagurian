using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Copilot;

class CopilotConfiguration : ShellConfiguration
{
    public override Element Render()
    {
        var clients = CopilotSettings.Clients(Settings);
        return FlexColumn(
            TextBlock("Enabled Copilot clients")
                .FontSize(12)
                .SemiBold()
                .Foreground(Theme.TextBrush),
            CheckBox(
                    clients.CopilotCli,
                    value => SetSettings(CopilotSettings.Write(
                        clients with { CopilotCli = value })),
                    label: "Copilot CLI")
                .Margin(0, 6, 0, 0),
            CheckBox(
                    clients.CopilotApp,
                    value => SetSettings(CopilotSettings.Write(
                        clients with { CopilotApp = value })),
                    label: "Copilot App"),
            CheckBox(
                    clients.VsCode,
                    value => SetSettings(CopilotSettings.Write(
                        clients with { VsCode = value })),
                    label: "VS Code"),
            TextBlock("These selections are stored for future client identification and do not change runtime behavior yet.")
                .FontSize(11)
                .Foreground(Theme.TextBrush)
                .Opacity(0.65)
                .TextWrapping(TextWrapping.Wrap)
                .Margin(0, 8, 0, 0));
    }
}

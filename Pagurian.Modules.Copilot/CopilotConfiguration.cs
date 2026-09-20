using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Pagurian.Sdk;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;
using static Microsoft.UI.Reactor.Factories;

namespace Pagurian.Modules.Copilot;

class CopilotConfiguration : ShellConfiguration
{
    public override Element Render()
    {
        var clients = CopilotSettings.Clients(Settings);

        return FlexColumn(
            Component<CopilotAccountPanel>()
                .Margin(0, 8, 0, 0),
            BodyStrong("Enabled Copilot clients")
                .HeadingLevel(AutomationHeadingLevel.Level2)
                .Margin(0, 16, 0, 0),
            CheckBox(
                    clients.CopilotCli,
                    value => SetSettings(CopilotSettings.Write(
                        clients with { CopilotCli = value })),
                    label: "Copilot CLI")
                .Margin(0, 8, 0, 0),
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
            Caption("These selections are stored for future client identification and do not change runtime behavior yet.")
                .TextWrapping(TextWrapping.WrapWholeWords)
                .Foreground(ReactorTheme.SecondaryText)
                .Margin(0, 8, 0, 0));
    }
}

class UsageConfiguration : ShellConfiguration
{
    public override Element Render()
    {
        var injected = UseContext(UsageConfigurationContext.Source);
        var model = UseMemo(() => injected is not null
            ? injected.CreateModel(Settings, message => Log.Warn(message))
            : new CopilotUsageModel(CopilotModule.Instance.Usage, Settings,
                message => Log.Warn(message),
                action => ReactorApp.UIDispatcher!.TryEnqueue(() => action())),
            injected, InstanceId);
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        var scheme = UseColorScheme();
        var theme = Theme;
        UseEffect(() =>
        {
            void Changed() => setVersion(++tick.Current);
            model.Changed += Changed;
            return () => { model.Changed -= Changed; model.Dispose(); };
        }, model);
        UseEffect(() =>
        {
            void Changed() => setVersion(++tick.Current);
            theme.Changed += Changed;
            return () => theme.Changed -= Changed;
        }, theme);
        UseEffect(() => model.ApplySettings(Settings), model, Settings?.GetRawText());

        return FlexColumn(
            CopilotAccountPanel.AccountSection(model.State,
                    injected?.Login ?? CopilotModule.Instance.Usage.Login)
                .Margin(0, 8, 0, 0),
            UsageCalendarView.Render(model.Pace, date =>
                SetSettings(model.Schedule.Toggle(date).Write(Settings)),
                theme.IsDark, scheme == ColorScheme.HighContrast)
                .Margin(0, 16, 0, 0));
    }
}

class CopilotAccountPanel : Component
{
    public override Element Render()
    {
        var service = CopilotModule.Instance.Usage;
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        UseEffect(() =>
        {
            void OnChanged() => setVersion(++tick.Current);
            service.Changed += OnChanged;
            return () => service.Changed -= OnChanged;
        }, service);

        return AccountSection(service.State, service.Login);
    }

    internal static Element AccountSection(
        CopilotUsageState state,
        Action login) =>
        Component<AccountSectionView, AccountSectionProps>(
            new(state, login));
}

internal sealed record AccountSectionProps(
    CopilotUsageState State,
    Action Login);

internal sealed class AccountSectionView : Component<AccountSectionProps>
{
    public override Element Render()
    {
        var (expanded, setExpanded) = UseState(false);
        var state = Props.State;
        var account = state.Account;
        var signedInAs = string.IsNullOrWhiteSpace(account?.Login)
            ? null
            : account.Login;
        var header = FlexRow(
                BodyStrong("Account")
                    .HeadingLevel(AutomationHeadingLevel.Level2)
                    .Flex(grow: 1, basis: 0),
                signedInAs is null
                    ? null
                    : BodyStrong($"Signed in as: {signedInAs}"))
            .HorizontalAlignment(HorizontalAlignment.Stretch);

        return Expander(
                header: "",
                content: AccountDetails(state, Props.Login),
                isExpanded: expanded,
                onIsExpandedChanged: setExpanded)
            .HeaderTemplate(header)
            .AutomationName(signedInAs is null
                ? "Account"
                : $"Account, signed in as {signedInAs}")
            .HorizontalAlignment(HorizontalAlignment.Stretch);
    }

    private static Element AccountDetails(
        CopilotUsageState state,
        Action login)
    {
        var status = AccountStatus(state);
        var statusTitle = BodyStrong(status.Title);
        if (status.IsError)
            statusTitle = statusTitle.Foreground(ReactorTheme.SystemCritical);

        return VStack(8,
                statusTitle,
                Body(status.Message)
                    .Foreground(ReactorTheme.SecondaryText)
                    .TextWrapping(TextWrapping.WrapWholeWords),
                ValueRow("Host", Display(state.Account?.Host)),
                ValueRow("Auth type", Display(state.Account?.AuthType)),
                string.IsNullOrWhiteSpace(state.Account?.Login)
                    ? Button("Login", login)
                        .AccessKey("L")
                        .IsEnabled(!state.IsLoggingIn)
                        .HorizontalAlignment(HorizontalAlignment.Left)
                        .Margin(0, 4, 0, 0)
                    : null)
            .Padding(12)
            .HorizontalAlignment(HorizontalAlignment.Stretch);
    }

    private static (
        string Title,
        string Message,
        bool IsError) AccountStatus(CopilotUsageState state)
    {
        if (state.IsLoggingIn)
        {
            return (
                "Signing in",
                "Finish the official GitHub Copilot CLI sign-in flow.",
                false);
        }

        if (state.Status == CopilotUsageStatus.Loading)
        {
            return (
                "Loading account",
                "Checking the current GitHub Copilot CLI account.",
                false);
        }

        if (state.Status == CopilotUsageStatus.Error)
        {
            return (
                "Account unavailable",
                string.IsNullOrWhiteSpace(state.Message)
                    ? "The GitHub Copilot CLI account could not be loaded."
                    : state.Message,
                true);
        }

        if (state.Status == CopilotUsageStatus.Unauthenticated)
        {
            return (
                "Not signed in",
                string.IsNullOrWhiteSpace(state.Message)
                    ? "GitHub Copilot CLI is not authenticated."
                    : state.Message,
                false);
        }

        if (state.IsRefreshing)
        {
            return (
                "Refreshing account",
                "Refreshing the current account and usage information.",
                false);
        }

        return (
            "Signed in",
            "GitHub Copilot CLI is authenticated.",
            false);
    }

    private static Element ValueRow(string label, string value) =>
        (Grid(
            [GridSize.Star(), GridSize.Star()],
            [GridSize.Auto],
            [
                Caption(label)
                    .Foreground(ReactorTheme.SecondaryText)
                    .TextWrapping(TextWrapping.WrapWholeWords)
                    .Grid(row: 0, column: 0),
                Body(value)
                    .TextAlignment(TextAlignment.Right)
                    .TextWrapping(TextWrapping.WrapWholeWords)
                    .Grid(row: 0, column: 1),
            ]) with
        {
            ColumnSpacing = 12,
        });

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "--" : value;
}

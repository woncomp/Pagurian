using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Pagurian.Sdk;
using static Microsoft.UI.Reactor.Factories;
using ReactorTheme = Microsoft.UI.Reactor.Core.Theme;

namespace Pagurian.Modules.Copilot;

// Account/quota remain shared; the workday comparison belongs to this shell.
class UsageBillboard : Billboard
{
    private readonly CopilotUsageModel _service;

    public UsageBillboard(CopilotUsageModel service) => _service = service;

    public override double WidthDip => 440;

    public override double HeightDip => 640;

    public override string Title => "GitHub Copilot Usage";

    public override void OnOpened() => _service.Refresh();

    public override Element Render()
    {
        var (_, setVersion) = UseState(0);
        var tick = UseRef(0);
        var colorScheme = UseColorScheme();

        UseEffect(() =>
        {
            void OnChanged() => setVersion(++tick.Current);
            _service.Changed += OnChanged;
            Theme.Changed += OnChanged;
            return () =>
            {
                _service.Changed -= OnChanged;
                Theme.Changed -= OnChanged;
            };
        }, _service);

        var highContrast = colorScheme == ColorScheme.HighContrast;
        var requestedTheme = highContrast
            ? ElementTheme.Default
            : Theme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        var state = _service.State;
        var usage = state.Status == CopilotUsageStatus.Ready
            ? state.Usage
            : CopilotUsageSnapshot.Empty;

        var scrollContent = VStack(8,
            ShouldShowStatusCard(state)
                ? StatusCard(state, highContrast)
                : null,
            AccountCard(state.Account, highContrast),
            PremiumInteractionsCard(usage.PremiumInteractions, highContrast),
            Component<UsagePaceCard, UsagePaceCardProps>(
                new(_service.Pace, Theme.IsDark, highContrast)));

        var content = FlexColumn(
            Subtitle("Copilot Usage")
                .HeadingLevel(AutomationHeadingLevel.Level1),
            (ScrollViewer(scrollContent) with
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollMode = ScrollMode.Disabled,
            })
                .Margin(0, 8, 0, 0)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .Flex(grow: 1, basis: 0));

        return Page(content, highContrast)
            .RequestedTheme(requestedTheme);
    }

    private BorderElement StatusCard(
        CopilotUsageState state,
        bool highContrast)
    {
        var status = StatusText(state);
        var heading = BodyStrong(status.Title)
            .HeadingLevel(AutomationHeadingLevel.Level2);
        if (status.IsError)
            heading = heading.Foreground(ReactorTheme.SystemCritical);

        return MaterialCard(
            VStack(8,
                heading,
                status.IsLoading
                    ? ProgressIndeterminate().Height(4)
                    : null,
                Body(status.Message)
                    .Foreground(ReactorTheme.SecondaryText)
                    .TextWrapping(TextWrapping.WrapWholeWords),
                status.IsError
                    ? Button("Retry", _service.Refresh)
                        .AccessKey("R")
                        .HorizontalAlignment(HorizontalAlignment.Left)
                    : null),
            highContrast);
    }

    private static (
        string Title,
        string Message,
        bool IsError,
        bool IsLoading) StatusText(CopilotUsageState state)
    {
        if (state.IsLoggingIn)
        {
            return (
                "Completing sign-in",
                "Finish the official GitHub Copilot CLI sign-in flow.",
                false,
                true);
        }

        if (state.Status == CopilotUsageStatus.Loading)
        {
            return (
                "Loading usage",
                "Checking the Copilot account and its current entitlements.",
                false,
                true);
        }

        if (state.Status == CopilotUsageStatus.Error)
        {
            return (
                "Unable to load usage",
                string.IsNullOrWhiteSpace(state.Message)
                    ? "Copilot usage is currently unavailable."
                    : state.Message,
                true,
                false);
        }

        if (state.Status == CopilotUsageStatus.Unauthenticated)
        {
            return (
                "Not signed in",
                string.IsNullOrWhiteSpace(state.Message)
                    ? "GitHub Copilot CLI is not authenticated."
                    : state.Message,
                false,
                false);
        }

        if (state.IsRefreshing)
        {
            return (
                "Refreshing usage",
                "Showing the last available values while usage is refreshed.",
                false,
                true);
        }

        if (state.Usage.IsEmpty)
        {
            return (
                "Usage data unavailable",
                "Premium interactions usage was not returned for this account.",
                false,
                false);
        }

        if (state.Usage.PremiumInteractions is null)
        {
            return (
                "Usage data unavailable",
                "Premium interactions usage was not returned for this account.",
                false,
                false);
        }

        if (state.Usage.PremiumInteractions.IsUnlimited)
        {
            return (
                "Unlimited entitlement",
                "Premium interactions has no metered limit.",
                false,
                false);
        }

        return (
            "Usage available",
            "Current Copilot usage and reset information is shown below.",
            false,
            false);
    }

    private static BorderElement AccountCard(
        CopilotUsageAccount? account,
        bool highContrast) =>
        MaterialCard(
            VStack(8,
                BodyStrong("Account")
                    .HeadingLevel(AutomationHeadingLevel.Level2),
                ValueRow("Username", Display(account?.Login))),
            highContrast);

    private static bool ShouldShowStatusCard(CopilotUsageState state) =>
        state.Status != CopilotUsageStatus.Ready ||
        state.IsRefreshing ||
        state.Usage.IsEmpty ||
        state.Usage.PremiumInteractions is null ||
        state.Usage.PremiumInteractions.IsUnlimited;

    private static BorderElement PremiumInteractionsCard(
        CopilotUsageQuota? category,
        bool highContrast)
    {
        var usedAndEntitlement = category switch
        {
            null => "Unavailable",
            { IsUnlimited: true } =>
                $"{FormatCount(category.UsedRequests)} / Unlimited",
            _ =>
                $"{FormatCount(category.UsedRequests)} / " +
                FormatCount(category.EntitlementRequests),
        };
        var reset = category switch
        {
            null => "--",
            _ => FormatReset(category.ResetDate),
        };

        return MaterialCard(
            VStack(8,
                BodyStrong("Credits")
                    .HeadingLevel(AutomationHeadingLevel.Level2),
                category is null
                    ? Caption("Unavailable")
                        .Foreground(ReactorTheme.SecondaryText)
                    : category.IsUnlimited
                        ? Caption("Unlimited entitlement")
                            .Foreground(ReactorTheme.SecondaryText)
                        : null,
                ValueRow("Used / entitlement", usedAndEntitlement),
                ValueRow("Reset time", reset)),
            highContrast);
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

    private static string FormatCount(long value) =>
        value.ToString("N0", CultureInfo.CurrentCulture);

    private static string FormatReset(DateTimeOffset? resetAt) =>
        resetAt is { } value
            ? value.ToLocalTime().ToString(
                "MMMM d, yyyy h:mm tt",
                CultureInfo.GetCultureInfo("en-US"))
            : "--";

    private static BorderElement Page(Element content, bool highContrast)
    {
        var page = Border(content)
            .Padding(8)
            .CornerRadius(8);

        return highContrast
            ? page
                .Background(ReactorTheme.Ref("SystemColorWindowColorBrush"))
                .WithBorder(
                    ReactorTheme.Ref("SystemColorWindowTextColorBrush"),
                    2)
                .Set(border =>
                    border.BackgroundSizing = BackgroundSizing.InnerBorderEdge)
            : page;
    }

    private static BorderElement MaterialCard(
        Element content,
        bool highContrast) =>
        Border(content)
            .Padding(12)
            .CornerRadius(8)
            .Background(highContrast
                ? ReactorTheme.Ref("SystemColorWindowColorBrush")
                : ReactorTheme.Ref("LayerOnAcrylicFillColorDefaultBrush"))
            .WithBorder(
                highContrast
                    ? ReactorTheme.Ref("SystemColorWindowTextColorBrush")
                    : ReactorTheme.SurfaceStroke,
                highContrast ? 2 : 1)
            .Set(border =>
                border.BackgroundSizing = BackgroundSizing.InnerBorderEdge);
}

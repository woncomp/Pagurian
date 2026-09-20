using System.Globalization;

namespace Pagurian.Modules.Copilot;

internal enum CopilotUsageStatus
{
    Loading,
    Unauthenticated,
    Ready,
    Error,
}

internal sealed record CopilotUsageAccount(
    string? Login,
    string? Host,
    string? AuthType);

internal sealed record CopilotUsageQuota(
    long UsedRequests,
    long EntitlementRequests,
    bool IsUnlimited,
    double? UsedPercentage,
    DateTimeOffset? ResetDate)
{
    public string PercentageText =>
        UsedPercentage is { } percentage
            ? Math.Round(percentage, MidpointRounding.AwayFromZero)
                .ToString("0", CultureInfo.InvariantCulture) + "%"
            : "--";
}

internal sealed record CopilotUsageSnapshot(CopilotUsageQuota? PremiumInteractions)
{
    public static CopilotUsageSnapshot Empty { get; } =
        new((CopilotUsageQuota?)null);

    public bool IsEmpty => PremiumInteractions is null;
}

internal sealed record CopilotUsageState(
    CopilotUsageStatus Status,
    CopilotUsageAccount? Account,
    CopilotUsageSnapshot Usage,
    string? Message,
    bool IsRefreshing,
    bool IsLoggingIn,
    DateTimeOffset? RefreshedAt)
{
    public static CopilotUsageState Initial { get; } = new(
        CopilotUsageStatus.Loading,
        null,
        CopilotUsageSnapshot.Empty,
        null,
        IsRefreshing: true,
        IsLoggingIn: false,
        RefreshedAt: null);

    public CopilotUsageState BeginRefresh() =>
        this with
        {
            Status = Status is CopilotUsageStatus.Error or
                CopilotUsageStatus.Unauthenticated ||
                Account is null && Usage.IsEmpty
                ? CopilotUsageStatus.Loading
                : Status,
            Message = null,
            IsRefreshing = true,
        };

    public CopilotUsageState BeginLogin() =>
        this with
        {
            Message = null,
            IsRefreshing = true,
            IsLoggingIn = true,
        };

    public CopilotUsageState WithAccount(CopilotUsageAccount account) =>
        this with { Account = account };

    public CopilotUsageState Complete(
        CopilotUsageAccount account,
        CopilotUsageSnapshot usage,
        DateTimeOffset refreshedAt,
        bool finishLogin) =>
        new(
            CopilotUsageStatus.Ready,
            account,
            usage,
            null,
            IsRefreshing: false,
            IsLoggingIn: finishLogin ? false : IsLoggingIn,
            refreshedAt);

    public CopilotUsageState CompleteUnauthenticated(
        CopilotUsageAccount account,
        string? message,
        DateTimeOffset refreshedAt,
        bool finishLogin) =>
        new(
            CopilotUsageStatus.Unauthenticated,
            account,
            CopilotUsageSnapshot.Empty,
            string.IsNullOrWhiteSpace(message)
                ? "GitHub Copilot CLI is not authenticated."
                : message,
            IsRefreshing: false,
            IsLoggingIn: finishLogin ? false : IsLoggingIn,
            refreshedAt);

    public CopilotUsageState Fail(string message, bool finishLogin) =>
        this with
        {
            Status = CopilotUsageStatus.Error,
            Message = message,
            IsRefreshing = false,
            IsLoggingIn = finishLogin ? false : IsLoggingIn,
        };
}

internal static class CopilotUsageNormalizer
{
    public static CopilotUsageQuota Normalize(
        long entitlementRequests,
        bool isUnlimitedEntitlement,
        long usedRequests,
        double remainingPercentage,
        DateTimeOffset? resetAt,
        DateTimeOffset now)
    {
        var unlimited = isUnlimitedEntitlement || entitlementRequests < 0;
        var entitlement = unlimited
            ? -1
            : Math.Max(0, entitlementRequests);
        var used = Math.Max(0, usedRequests);

        double? usedPercentage = null;
        if (!unlimited)
        {
            if (double.IsFinite(remainingPercentage))
            {
                usedPercentage = Math.Clamp(100 - remainingPercentage, 0, 100);
            }
            else if (entitlement > 0)
            {
                usedPercentage = Math.Clamp(
                    used * 100d / entitlement,
                    0,
                    100);
            }
        }

        return new CopilotUsageQuota(
            used,
            entitlement,
            unlimited,
            usedPercentage,
            NormalizeResetDate(resetAt, now));
    }

    private static DateTimeOffset NormalizeResetDate(
        DateTimeOffset? resetAt,
        DateTimeOffset now)
    {
        if (resetAt is { } value && value > now)
            return value;

        var currentMonth = new DateTimeOffset(
            now.Year,
            now.Month,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        return currentMonth.AddMonths(1);
    }
}

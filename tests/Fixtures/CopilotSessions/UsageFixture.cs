using Pagurian.Modules.Copilot;

static class UsageFixture
{
    public static void Run()
    {
        var reset = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var now = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
        var metered = CopilotUsageNormalizer.Normalize(
            entitlementRequests: 100,
            isUnlimitedEntitlement: false,
            usedRequests: 25,
            remainingPercentage: 75,
            reset,
            now);
        Check(metered.UsedRequests == 25);
        Check(metered.EntitlementRequests == 100);
        Check(metered.UsedPercentage == 25);
        Check(metered.PercentageText == "25%");
        Check(metered.ResetDate == reset);

        var clamped = CopilotUsageNormalizer.Normalize(
            entitlementRequests: 10,
            isUnlimitedEntitlement: false,
            usedRequests: 99,
            remainingPercentage: -20,
            reset,
            now);
        Check(clamped.UsedPercentage == 100);

        var derived = CopilotUsageNormalizer.Normalize(
            entitlementRequests: 40,
            isUnlimitedEntitlement: false,
            usedRequests: 10,
            remainingPercentage: double.NaN,
            reset,
            now);
        Check(derived.UsedPercentage == 25);

        var unlimited = CopilotUsageNormalizer.Normalize(
            entitlementRequests: -1,
            isUnlimitedEntitlement: false,
            usedRequests: 500,
            remainingPercentage: 0,
            reset,
            now);
        Check(unlimited.IsUnlimited);
        Check(unlimited.EntitlementRequests == -1);
        Check(unlimited.UsedPercentage is null);
        Check(unlimited.PercentageText == "--");
        Check(unlimited.ResetDate == reset);

        var expired = CopilotUsageNormalizer.Normalize(
            entitlementRequests: 100,
            isUnlimitedEntitlement: false,
            usedRequests: 25,
            remainingPercentage: 75,
            resetAt: now,
            now: now);
        Check(expired.ResetDate == reset);

        var missing = CopilotUsageNormalizer.Normalize(
            entitlementRequests: 100,
            isUnlimitedEntitlement: false,
            usedRequests: 25,
            remainingPercentage: 75,
            resetAt: null,
            now);
        Check(missing.ResetDate == reset);

        var december = DateTimeOffset.Parse("2026-12-31T23:00:00Z");
        var decemberFallback = CopilotUsageNormalizer.Normalize(
            entitlementRequests: 100,
            isUnlimitedEntitlement: false,
            usedRequests: 25,
            remainingPercentage: 75,
            resetAt: december,
            now: december);
        Check(decemberFallback.ResetDate ==
            DateTimeOffset.Parse("2027-01-01T00:00:00Z"));

        var account = new CopilotUsageAccount(
            "octocat",
            "github.com",
            "user");
        var usage = new CopilotUsageSnapshot(
            unlimited);
        var ready = CopilotUsageState.Initial.Complete(
            account,
            usage,
            reset,
            finishLogin: false);
        var refreshing = ready.BeginRefresh();
        Check(refreshing.Status == CopilotUsageStatus.Ready);
        Check(refreshing.IsRefreshing);
        Check(refreshing.Usage.PremiumInteractions == unlimited);

        var failed = refreshing.Fail("offline", finishLogin: false);
        Check(failed.Status == CopilotUsageStatus.Error);
        Check(ReferenceEquals(failed.Usage, usage));
        Check(failed.Account == account);
        var retrying = failed.BeginRefresh();
        Check(retrying.Status == CopilotUsageStatus.Loading);
        Check(retrying.IsRefreshing);
        Check(retrying.Message is null);

        var unauthenticated = ready.CompleteUnauthenticated(
            new CopilotUsageAccount(null, "github.com", null),
            null,
            reset,
            finishLogin: true);
        Check(unauthenticated.Status == CopilotUsageStatus.Unauthenticated);
        Check(unauthenticated.Usage.IsEmpty);
        Check(!unauthenticated.IsLoggingIn);
        Check(unauthenticated.Message is not null);
    }

    private static void Check(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Usage fixture assertion failed.");
    }
}

using Pagurian.Modules.Copilot;

static class UsageHistoryFixture
{
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("Usage history fixture +08", TimeSpan.FromHours(8),
            "Usage history fixture", "Usage history fixture");
    private static readonly DateTimeOffset Reset = new(2026, 10, 1, 8, 0, 0, TimeSpan.FromHours(8));
    private static readonly CopilotUsageAccount Account = new("octocat", "github.com", "user");
    private static int _checks;

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "pagurian-usage-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CopilotUsageHistoryStore(root);
            Check(store.Read(Account).Cycles.Count == 0, "new account should have no history");
            var other = Account with { Login = "another" };
            Check(store.PathFor(Account) != store.PathFor(other), "account filenames collide");
            Check(store.PathFor(Account) == store.PathFor(Account with { Login = " OCTOCAT " }),
                "account filename is not normalized");

            void Sample(int day, int hour, long used, long entitlement = 1_000)
            {
                var at = new DateTimeOffset(2026, 9, day, hour, 0, 0, TimeSpan.FromHours(8));
                store.Record(Account, Quota(used, Reset, entitlement), at, Zone);
            }
            Sample(21, 8, 100);
            Sample(21, 23, 110);
            Sample(22, 8, 130);
            Sample(22, 22, 140);
            Sample(25, 8, 180);
            Sample(25, 20, 190);
            Sample(28, 8, 220);
            Sample(28, 18, 240);
            var history = new CopilotUsageHistoryStore(root).Read(Account);
            Check(history.Cycles.Count == 1 && history.Cycles[0].Entitlement == 1_000,
                "cycle entitlement was not persisted");
            Check(history.Cycles[0].Days[0].FirstUsed == 100 &&
                history.Cycles[0].Days[0].LastUsed == 110, "first/last were not retained");
            Check(store.Read(other).Cycles.Count == 0, "history leaked across accounts");

            var now = new DateTimeOffset(2026, 9, 28, 21, 0, 0, TimeSpan.FromHours(8));
            CopilotUsageHistoryCell Day(int date, CopilotUsageSettings? settings = null) =>
                CopilotUsageHistoryCalculator.Calculate(history,
                    settings ?? CopilotUsageSettings.Default, now, Zone)
                    .Days.Single(cell => cell.Date == new DateOnly(2026, 9, date));
            Check(Day(21).Used == 30 && !Day(21).Estimated,
                "adjacent first-to-first delta missing");
            Check(Day(22).Used == 10 && Day(25).Used == 10,
                "gap boundary should use the same day's last sample");
            Check(Day(23).Used == 20 && Day(24).Used == 20 && Day(23).Estimated,
                "missing workdays not estimated evenly");
            Check(Day(26).Used is null && Day(27).Used is null,
                "unobserved weekend was incorrectly attributed");
            Check(Day(28).Used == 20 && !Day(28).Estimated,
                "today must use its latest sample");
            Check(Day(21).StartUsed == 100 && Day(22).StartUsed == 130 &&
                Day(25).StartUsed == 180 && Day(28).StartUsed == 220 &&
                !Day(28).StartEstimated,
                "starting totals must use each day's first sample, not its last or delta");
            Check(Day(23).StartUsed == 140 && Day(24).StartUsed == 160 &&
                Day(23).StartEstimated && Day(24).StartEstimated,
                "estimated starting totals must accumulate the allocated gap usage");
            Check(Day(20).StartUsed is null && Day(26).StartUsed is null,
                "unobserved days must not claim a known starting total");
            Check(Day(21).PercentOfEntitlement == 3,
                "stored cycle entitlement was not used");
            var saturday = CopilotUsageSettings.Default.Toggle(new DateOnly(2026, 9, 26));
            Check(Day(26, saturday).Used == 30 && Day(26, saturday).Estimated &&
                Day(27, saturday).Used is null,
                "workday override did not redistribute a gap");
            Check(Day(26, saturday).StartUsed == 190 && Day(26, saturday).StartEstimated,
                "overridden workday must start at the gap's last observed total");
            var window = CopilotUsageHistoryCalculator.Calculate(
                history, CopilotUsageSettings.Default, now, Zone);
            Check(window.Days.Count == CopilotUsageHistoryCalculator.HistoryDays &&
                window.Days[0].Date == window.Today.AddDays(-59) &&
                window.Days[^1].Date == window.Today,
                "history window must contain exactly the 60 days through today");

            var newReset = Reset.AddMonths(1);
            var newCycle = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(8));
            store.Record(Account, Quota(5, newReset, 0), newCycle, Zone);
            store.Record(Account, Quota(12, newReset, 0), newCycle.AddHours(2), Zone);
            history = store.Read(Account);
            Check(history.Cycles.Count == 2 && history.Cycles[1].Entitlement is null,
                "cycle entitlement should remain unknown when absent");
            var projected = CopilotUsageHistoryCalculator.Calculate(history,
                CopilotUsageSettings.Default, newCycle.AddHours(3), Zone);
            Check(projected.Days.Single(day => day.Date == new DateOnly(2026, 10, 1))
                    .PercentOfEntitlement == 7d / 75_000 * 100,
                "missing cycle entitlement did not use 75,000");
            Check(projected.Days.Single(day => day.Date == new DateOnly(2026, 9, 29)).Used is null,
                "a gap was estimated across reset");
            Check(projected.Days.Single(day => day.Date == new DateOnly(2026, 10, 1)).StartUsed == 5 &&
                projected.Days.Single(day => day.Date == new DateOnly(2026, 9, 29)).StartUsed is null,
                "starting total must not carry over or interpolate across a cycle reset");

            var mixedRoot = Path.Combine(root, "same-day");
            var mixed = new CopilotUsageHistoryStore(mixedRoot);
            var oldReset = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.FromHours(8));
            var nextReset = oldReset.AddMonths(1);
            var before = oldReset.AddHours(-4);
            mixed.Record(Account, Quota(100, oldReset, 1_000), before, Zone);
            mixed.Record(Account, Quota(108, oldReset, 1_000), before.AddHours(1), Zone);
            mixed.Record(Account, Quota(2, nextReset, 1_000), oldReset.AddHours(1), Zone);
            mixed.Record(Account, Quota(7, nextReset, 1_000), oldReset.AddHours(2), Zone);
            projected = CopilotUsageHistoryCalculator.Calculate(mixed.Read(Account),
                CopilotUsageSettings.Default, oldReset.AddHours(3), Zone);
            Check(projected.Days.Single(day => day.Date == new DateOnly(2026, 9, 28)).Used == 13,
                "two cycles on one local day should add independent deltas");
            var reversed = mixed.Read(Account) with { Cycles = mixed.Read(Account).Cycles.Reverse().ToArray() };
            var resetDay = CopilotUsageHistoryCalculator.Calculate(reversed,
                CopilotUsageSettings.Default, oldReset.AddHours(3), Zone).Days
                .Single(day => day.Date == new DateOnly(2026, 9, 28));
            Check(resetDay.StartUsed == 100 && !resetDay.StartEstimated,
                "same-day cycles must retain the earliest starting counter regardless of file order");
            var decreasing = new CopilotUsageHistoryState([new(oldReset.AddMonths(-1), oldReset, 1_000,
                [new(new(2026, 9, 28), before, 100, before.AddHours(1), 90)])]);
            var decreasingDay = CopilotUsageHistoryCalculator.Calculate(decreasing,
                CopilotUsageSettings.Default, oldReset, Zone).Days[^1];
            Check(decreasingDay.Used is null && decreasingDay.StartUsed == 100,
                "an invalid daily delta must not discard its known starting sample");
            var partialReset = decreasing with { Cycles = [
                decreasing.Cycles[0], mixed.Read(Account).Cycles.Single(cycle => cycle.Reset == nextReset)] };
            var partialDay = CopilotUsageHistoryCalculator.Calculate(partialReset,
                CopilotUsageSettings.Default, oldReset.AddHours(3), Zone).Days[^1];
            Check(partialDay.Used == 5 && partialDay.StartUsed == 100,
                "retaining a baseline must not suppress another cycle's valid same-day usage");

            var corruptPath = store.PathFor(other);
            Directory.CreateDirectory(root);
            File.WriteAllText(corruptPath, """{"Version":1,"Cycles":[{"Start":"2026-09-01T08:00:00+08:00","Reset":"2026-10-01T08:00:00+08:00","Days":null}]}""");
            var original = File.ReadAllText(corruptPath);
            Throws<InvalidDataException>(() => store.Read(other));
            Throws<InvalidDataException>(() => store.Record(other, Quota(10, Reset), now, Zone));
            Check(File.ReadAllText(corruptPath) == original,
                "corrupt history was overwritten");
            var obstructed = new CopilotUsageHistoryStore(Path.Combine(root, "blocked"));
            Directory.CreateDirectory(obstructed.PathFor(other));
            ThrowsIo(() => obstructed.Record(other, Quota(10, Reset), now, Zone));
            Console.WriteLine($"  usage history: {_checks} assertions");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static CopilotUsageQuota Quota(long used, DateTimeOffset reset, long entitlement = 1_000) =>
        new(used, entitlement, entitlement <= 0, null, reset);

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        _checks++;
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void ThrowsIo(Action action)
    {
        _checks++;
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected a history write failure.");
    }
}

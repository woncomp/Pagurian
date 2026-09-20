using Pagurian.Modules.Copilot;
using System.Runtime.CompilerServices;
using System.Text.Json;

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

        CalendarAndSettings();
        CyclesAndTimeZones();
        PaceAndLabels();
        RuntimeAndFreshness();
        ClockDisposal();
        Console.WriteLine($"Copilot Usage calendar/settings/pace/runtime: PASS ({_checks} assertions).");
    }

    private static readonly DateTimeOffset SeptemberNow = DateTimeOffset.Parse("2026-09-10T04:00:00Z");
    private static readonly DateTimeOffset OctoberReset = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
    private static readonly TimeZoneInfo China = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private static readonly CopilotUsageAccount Account = new("fixture", "github.com", "fake");
    private static int _checks;

    private static CopilotUsageState Ready(double? percent = 25, DateTimeOffset? reset = null,
        DateTimeOffset? read = null, bool unlimited = false) =>
        new(CopilotUsageStatus.Ready, Account,
            new(new(25, 100, unlimited, percent, reset ?? OctoberReset,
                reset ?? OctoberReset, false, read ?? SeptemberNow)),
            null, false, false, read ?? SeptemberNow);

    private static CopilotUsagePace Pace(CopilotUsageState? state = null,
        CopilotUsageSettings? settings = null, DateTimeOffset? now = null, TimeZoneInfo? zone = null) =>
        CopilotUsageCalendar.Calculate(state ?? Ready(), settings ?? CopilotUsageSettings.Default,
            now ?? SeptemberNow, zone ?? China);

    private static void NoWarning(string message) => throw new InvalidOperationException(message);
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    private static void CalendarAndSettings()
    {
        var defaults = CopilotUsageSettings.Default;
        var p = Pace();
        Check(p.TotalWorkdays == 22 && p.ElapsedWorkdays == 8);
        Check(p.Cycle!.Days.Count == 35);
        Check(p.Cycle.Days[0].Date == new DateOnly(2026, 8, 30));
        Check(p.Cycle.Days[0].Date.DayOfWeek == DayOfWeek.Sunday);
        Check(p.Cycle.Days[^1].Date == new DateOnly(2026, 10, 3));
        Check(p.Cycle.Days[^1].Date.DayOfWeek == DayOfWeek.Saturday);
        Check(p.Cycle.EligibleDays.First().Date == new DateOnly(2026, 9, 1));
        Check(p.Cycle.EligibleDays.Last().Date == new DateOnly(2026, 9, 30));
        Check(p.Cycle.Days.Count(day => day.IsToday) == 1);
        var today = new DateOnly(2026, 9, 10);
        var saturday = new DateOnly(2026, 9, 12);
        var overridden = defaults.Toggle(today).Toggle(saturday);
        Check(!overridden.IsWorkday(today) && overridden.IsWorkday(saturday));
        Check(Pace(settings: overridden).TotalWorkdays == 22);
        Check(Pace(settings: overridden).ElapsedWorkdays == 7);
        Check(Pace(settings: defaults.Toggle(new(2026, 9, 1))).ElapsedWorkdays == 7);
        Check(Pace(settings: defaults.Toggle(new(2026, 9, 30))).ElapsedWorkdays == 8);
        Check(defaults.IsWorkday(today) && !defaults.IsWorkday(saturday));
        var unrelated = Json("""{"clients":{"cli":false},"other":[1,2],"workdayOverrides":{"2025-01-01":false}}""");
        var saved = CopilotUsageSettings.Read(unrelated, NoWarning).Toggle(today).Write(unrelated);
        Check(saved.GetProperty("clients").GetProperty("cli").GetBoolean() == false);
        Check(saved.GetProperty("other").GetArrayLength() == 2);
        Check(saved.GetProperty("workdayOverrides").GetProperty("2025-01-01").GetBoolean() == false);
        Check(saved.GetProperty("workdayOverrides").GetProperty("2026-09-10").GetBoolean() == false);
        Check(!CopilotUsageSettings.Read(saved, NoWarning).IsWorkday(today));
        var restored = overridden.Toggle(today).Toggle(saturday).Write(null);
        Check(!restored.GetProperty("workdayOverrides").EnumerateObject().Any());
        Check(overridden.IsWorkday(new DateOnly(2026, 10, 10)) == defaults.IsWorkday(new(2026, 10, 10)));
        Check(Pace(settings: CopilotUsageSettings.Read(unrelated, NoWarning)).TotalWorkdays == 22);
        var warnings = new List<string>();
        Check(CopilotUsageSettings.Read(null, warnings.Add).IsWorkday(today));
        CopilotUsageSettings.Read(Json("null"), warnings.Add);
        Check(warnings.Count == 0);
        CopilotUsageSettings.Read(Json("[]"), warnings.Add);
        CopilotUsageSettings.Read(Json("""{"workdayOverrides":[]}"""), warnings.Add);
        var partiallyValid = CopilotUsageSettings.Read(Json("""
            {"workdayOverrides":{"2026-09-10":false,"bad-secret-date":true,
            "2026-02-30":true,"2026-09-12":"true","2026-9-13":true,"2026-09-14":true}}
            """), warnings.Add);
        Check(warnings.Count == 6);
        Check(warnings.All(warning => !warning.Contains("secret")));
        Check(!partiallyValid.IsWorkday(today));
        Check(partiallyValid.Write(null).GetProperty("workdayOverrides").EnumerateObject().Count() == 1);
    }

    private static void CyclesAndTimeZones()
    {
        static CopilotUsageCycle Bounds(string reset, TimeZoneInfo zone)
        {
            var at = DateTimeOffset.Parse(reset);
            return CopilotUsageCalendar.Cycle(new(0, 100, false, 0, at),
                CopilotUsageSettings.Default, at.AddDays(-10), zone)!;
        }
        static void Dates(CopilotUsageCycle cycle, DateOnly first, DateOnly last)
        {
            Check(cycle.EligibleDays.First().Date == first);
            Check(cycle.EligibleDays.Last().Date == last);
            Check(cycle.Days.Count % 7 == 0);
            foreach (var day in cycle.EligibleDays)
                Check(day.Date >= first && day.Date <= last);
        }
        Dates(Bounds("2026-10-01T00:00:00Z", Pacific), new(2026, 8, 31), new(2026, 9, 29));
        Dates(Bounds("2026-10-01T00:00:00+08:00", China), new(2026, 9, 1), new(2026, 9, 30));
        Dates(Bounds("2026-10-01T08:00:00+08:00", China), new(2026, 9, 1), new(2026, 9, 30));
        Dates(Bounds("2026-10-01T00:00:00Z", China), new(2026, 9, 1), new(2026, 9, 30));
        // Exact end-of-day is excluded at reset and included at start.
        Dates(Bounds("2026-09-30T23:59:59.9999999+08:00", China), new(2026, 8, 30), new(2026, 9, 29));
        var endOfMarch = Bounds("2026-03-31T00:00:00Z", TimeZoneInfo.Utc);
        Check(endOfMarch.Start == DateTimeOffset.Parse("2026-02-28T00:00:00Z"));
        Dates(endOfMarch, new(2026, 2, 28), new(2026, 3, 30));
        Dates(Bounds("2024-03-01T00:00:00Z", TimeZoneInfo.Utc), new(2024, 2, 1), new(2024, 2, 29));
        Dates(Bounds("2025-03-01T00:00:00Z", TimeZoneInfo.Utc), new(2025, 2, 1), new(2025, 2, 28));
        Dates(Bounds("2027-01-01T00:00:00Z", TimeZoneInfo.Utc), new(2026, 12, 1), new(2026, 12, 31));
        Check(CopilotUsageCalendar.LocalDayEnd(new(2026, 3, 7), Eastern).Offset == TimeSpan.FromHours(-5));
        Check(CopilotUsageCalendar.LocalDayEnd(new(2026, 3, 8), Eastern).Offset == TimeSpan.FromHours(-4));
        Check(CopilotUsageCalendar.LocalDayEnd(new(2026, 10, 31), Eastern).Offset == TimeSpan.FromHours(-4));
        Check(CopilotUsageCalendar.LocalDayEnd(new(2026, 11, 1), Eastern).Offset == TimeSpan.FromHours(-5));
        var lateGap = TimeZoneInfo.CreateCustomTimeZone("fixture-end-of-day-DST", TimeSpan.Zero,
            "fixture", "standard", "daylight",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 23, 0, 0), 3, 31),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 23, 0, 0), 10, 31))]);
        Check(CopilotUsageCalendar.LocalDayEnd(new(2026, 3, 31), lateGap) ==
            DateTimeOffset.Parse("2026-03-31T22:59:59.9999999Z"));
        Check(Bounds("2026-04-01T00:00:00+01:00", lateGap).EligibleDays.Last().Date == new DateOnly(2026, 3, 31));
        var ambiguousEnd = TimeZoneInfo.CreateCustomTimeZone("fixture-ambiguous-end", TimeSpan.Zero,
            "fixture", "standard", "daylight",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 11, 1))]);
        Check(CopilotUsageCalendar.LocalDayEnd(new(2026, 10, 31), ambiguousEnd).Offset == TimeSpan.Zero);
        // Subtraction before conversion matters here: March 1 00:00 -04 is
        // February 28 23:00 in New York, so February 28 belongs to the cycle.
        Dates(Bounds("2026-04-01T00:00:00-04:00", Eastern), new(2026, 2, 28), new(2026, 3, 31));
        Dates(Bounds("2026-12-01T00:00:00-05:00", Eastern), new(2026, 11, 1), new(2026, 11, 30));
        var offsetNow = DateTimeOffset.Parse("2026-10-01T00:30:00+08:00");
        var fallback = CopilotUsageNormalizer.Normalize(100, false, 25, 75, null, offsetNow);
        Check(fallback.ResetDate == OctoberReset && fallback.ResetIsEstimated);
        Check(fallback.RawResetDate is null);
        var expired = CopilotUsageNormalizer.Normalize(100, false, 25, 75,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"), offsetNow);
        Check(expired.ResetDate == OctoberReset && expired.ResetIsEstimated);
        Check(expired.RawResetDate == DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var previousOffsetDay = DateTimeOffset.Parse("2026-12-31T23:30:00-08:00");
        Check(CopilotUsageNormalizer.Normalize(100, false, 25, 75, null, previousOffsetDay).ResetDate ==
            DateTimeOffset.Parse("2027-02-01T00:00:00Z"));
        var valid = CopilotUsageNormalizer.Normalize(100, false, 25, 75, OctoberReset, SeptemberNow);
        Check(!valid.ResetIsEstimated && valid.RawResetDate == OctoberReset);
    }

    private static void PaceAndLabels()
    {
        var first = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        Check(Pace(now: first, state: Ready(0, read: first)).ElapsedWorkdays == 1);
        Check(Pace(now: first.AddHours(15), state: Ready(0, read: first)).ElapsedWorkdays == 1);
        var last = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        Check(Pace(now: last).ElapsedWorkdays == 22);
        Check(Pace(now: last).WorkdayPercentage == 100);
        var sunday = Pace(now: DateTimeOffset.Parse("2026-09-13T04:00:00Z"));
        Check(sunday.ElapsedWorkdays == 9 && sunday.IsRestDay);
        Check(Pace(now: DateTimeOffset.Parse("2026-09-11T04:00:00Z")).ElapsedWorkdays == sunday.ElapsedWorkdays);
        // August starts on a Saturday: do not call zero the "0th day".
        var august = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var zero = Pace(Ready(0, DateTimeOffset.Parse("2026-09-01T00:00:00Z"), august), now: august);
        Check(zero.ElapsedWorkdays == 0 && zero.WorkdayLabel.StartsWith("0 / "));
        var allRest = CopilotUsageSettings.Default;
        foreach (var day in Pace().Cycle!.EligibleDays.Where(day => day.IsWorkday))
            allRest = allRest.Toggle(day.Date);
        var noDays = Pace(settings: allRest);
        Check(noDays.TotalWorkdays == 0 && noDays.ElapsedWorkdays == 0);
        Check(noDays.WorkdayPercentage is null && noDays.RawBalance is null);
        Check(noDays.BalanceText == "No workdays configured");
        Check(noDays.Cycle!.Days.Count == 35);
        var normal = Pace(Ready(25.125));
        Check(normal.UsedPercentage == 25.125);
        Check(Math.Abs(normal.RawBalance!.Value - (8 - .25125 * 22)) < 1e-10);
        foreach (var (raw, rounded) in new (double, int)[]
        {
            (-6.5, -7), (-6, -6), (-.5, -1), (-.49, 0), (0, 0),
            (.49, 0), (.5, 1), (6, 6), (6.5, 7),
        })
            Check(CopilotUsageCalendar.RoundBalance(raw) == rounded);
        foreach (var balance in new[] { -22, -7, -6, -4, -1, 0, 1, 4, 6, 7, 22 })
        {
            var display = normal with { RoundedBalance = balance };
            Check(display.TintWeight == Math.Min(Math.Abs(balance) / 6d, 1));
            Check(display.BalanceText == (balance == 0 ? "Your credit usage is on track." :
                $"{(balance > 0 ? "You have a surplus of" : "You’re over budget by")} {Math.Abs(balance)} {(Math.Abs(balance) == 1 ? "day’s" : "days’")} worth of credits."));
        }
        foreach (var (value, ordinal) in new (int, string)[]
        {
            (1,"1st"), (2,"2nd"), (3,"3rd"), (4,"4th"), (11,"11th"),
            (12,"12th"), (13,"13th"), (21,"21st"), (22,"22nd"), (23,"23rd"), (111,"111th"),
        })
            Check(CopilotUsageCalendar.Ordinal(value) == ordinal);
        foreach (var width in new[] { 16d, 100, 336, 500 })
            foreach (var percentage in new[] { -1d, 0, .1, 25.125, 99.9, 100, 101 })
            {
                var marker = CopilotUsageCalendar.MarkerPosition(percentage, width, 8);
                Check(marker == 8 + width * Math.Clamp(percentage, 0, 100) / 100);
                foreach (var labelWidth in new[] { 12d, 100, 1000 })
                {
                    var x = CopilotUsageCalendar.ClampLabel(marker, labelWidth, width + 16);
                    Check(x >= 0 && x + Math.Min(labelWidth, width + 16) <= width + 16);
                }
            }
        Check(CopilotUsageCalendar.ClampLabel(50, 20, 100) == 40);
        foreach (var state in new[]
        {
            CopilotUsageState.Initial, Ready() with { Status = CopilotUsageStatus.Unauthenticated },
            Ready() with { Status = CopilotUsageStatus.Error }, Ready() with { IsLoggingIn = true },
            Ready(null), Ready(10, unlimited: true), Ready(double.NaN),
            Ready() with { Usage = CopilotUsageSnapshot.Empty },
            Ready() with { Usage = new(new(25, 100, false, 25, null)) },
        })
        {
            var unavailable = Pace(state);
            Check(unavailable.RoundedBalance is null && unavailable.TintWeight == 0);
            Check(unavailable.BalanceText != "Your credit usage is on track.");
        }
        Check(Pace(Ready(null)).TotalWorkdays == 22);
        Check(Pace(Ready(10, unlimited: true)).TotalWorkdays == 22);
        Check(Pace(Ready() with { IsRefreshing = true }).UsedPercentage == 25);
        Check(Pace(now: OctoberReset).UsedPercentage is null);
        var nextCycle = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        Check(Pace(Ready(25, nextCycle, SeptemberNow), now: OctoberReset).UsedPercentage is null);
        Check(Pace(Ready(0, nextCycle, OctoberReset), now: OctoberReset).UsedPercentage == 0);
        var fallback = CopilotUsageNormalizer.Normalize(100, false, 25, 75, null, SeptemberNow);
        var estimated = Pace(Ready() with { Usage = new(fallback) });
        Check(estimated.Cycle!.IsEstimated && estimated.UsedPercentage == 25);
        foreach (var invalidReset in new[] { SeptemberNow, SeptemberNow.AddDays(-1) })
        {
            var fetched = CopilotUsageNormalizer.Normalize(100, false, 25, 75,
                invalidReset, SeptemberNow) with { ReadStartedAt = SeptemberNow };
            var fallbackPace = Pace(Ready() with { Usage = new(fetched) });
            Check(fallbackPace.Cycle!.IsEstimated && fallbackPace.UsedPercentage == 25);
            Check(fallbackPace.RoundedBalance == estimated.RoundedBalance);
        }
    }

    private static void RuntimeAndFreshness()
    {
        var source = new FakeUsageSource(Ready());
        var now = SeptemberNow;
        var zone = China;
        var queue = new Queue<Action>();
        CopilotUsageModel NewModel(JsonElement? settings = null) =>
            new(source, settings, NoWarning, queue.Enqueue, () => now, () => zone, observeClock: false);
        void Drain() { while (queue.TryDequeue(out var callback)) callback(); }
        var a = NewModel();
        var b = NewModel();
        int changedA = 0, changedB = 0;
        a.Changed += () => changedA++;
        b.Changed += () => changedB++;
        Check(source.Subscribers == 2);
        var draft = a.Schedule.Toggle(new(2026, 9, 10)).Write(Json("""{"preserve":42}"""));
        Check(a.Pace.ElapsedWorkdays == 8 && b.Pace.ElapsedWorkdays == 8);
        a.ApplySettings(draft); // host Save
        Check(a.Pace.ElapsedWorkdays == 7 && b.Pace.ElapsedWorkdays == 8);
        Check(changedA == 1 && changedB == 0);
        a.ObserveClock();
        Check(changedA == 1);
        now = now.AddSeconds(15);
        a.ObserveClock();
        Check(changedA == 1 && source.Refreshes == 0);
        now = now.AddDays(1); // resume and local date advance, no SDK per tick
        a.ObserveClock(); b.ObserveClock();
        Check(a.Pace.ElapsedWorkdays == 8 && b.Pace.ElapsedWorkdays == 9);
        Check(source.Refreshes == 0);
        now = DateTimeOffset.Parse("2026-09-12T00:01:00+08:00");
        a.ObserveClock();
        Check(a.Pace.Today == new DateOnly(2026, 9, 12) && a.Pace.IsRestDay);
        zone = Pacific; a.ObserveClock();
        Check(a.Pace.Today == new DateOnly(2026, 9, 11));
        Check(a.Pace.Cycle!.EligibleDays.First().Date == new DateOnly(2026, 8, 31));
        var beforeResume = changedA;
        now = now.AddMinutes(3); a.ObserveClock();
        Check(changedA == beforeResume + 1);
        zone = China;
        now = OctoberReset;
        a.ObserveClock();
        Check(a.Pace.UsedPercentage is null && a.Pace.BalanceText.Contains("Refreshing"));
        Check(source.Refreshes == 1);
        a.ObserveClock();
        Check(source.Refreshes == 1);
        var beforePublish = a.State;
        source.Publish(source.State.BeginRefresh());
        Check(a.State == beforePublish && !a.State.IsRefreshing);
        Drain();
        Check(a.State.IsRefreshing);
        Check(a.Pace.UsedPercentage is null && b.Pace.UsedPercentage is null);
        // Request started before reset and completed afterward: effective
        // fallback changes but no comparison may use the old quota.
        var crossing = CopilotUsageNormalizer.Normalize(100, false, 90, 10, null, now) with
            { ReadStartedAt = now.AddSeconds(-1) };
        source.Publish(Ready() with { Usage = new(crossing), RefreshedAt = now }); Drain();
        Check(a.Pace.UsedPercentage is null);
        now = now.AddMinutes(1); a.ObserveClock();
        Check(source.Refreshes >= 2);
        var expiredServer = CopilotUsageNormalizer.Normalize(100, false, 90, 10, OctoberReset, now);
        source.Publish(Ready() with { Usage = new(expiredServer), RefreshedAt = now }); Drain();
        Check(a.Pace.UsedPercentage == 90 && a.Pace.Cycle!.IsEstimated);
        var refreshes = source.Refreshes;
        now = now.AddMinutes(1); a.ObserveClock();
        Check(source.Refreshes == refreshes); // invalid raw reset must not cause a retry loop
        var fresh = CopilotUsageNormalizer.Normalize(100, false, 0, 100, null, now);
        source.Publish(Ready() with { Usage = new(fresh), RefreshedAt = now }); Drain();
        Check(a.Pace.UsedPercentage == 0 && a.Pace.Cycle!.IsEstimated);
        Check(a.Pace.TotalWorkdays == b.Pace.TotalWorkdays); // September override does not recur
        source.Publish(source.State); // pending UI callbacks
        var finalChanges = changedA;
        a.Dispose(); a.Dispose();
        Drain();
        Check(changedA == finalChanges && source.Subscribers == 1);
        a.ApplySettings(null); a.ObserveClock(); a.Refresh();
        Check(changedA == finalChanges);
        b.Dispose();
        Check(source.Subscribers == 0);
    }

    private sealed class FakeUsageSource(CopilotUsageState state) : ICopilotUsageSource
    {
        private Action? _changed;
        public int Subscribers { get; private set; }
        public int Refreshes { get; private set; }
        public CopilotUsageState State { get; private set; } = state;
        public event Action? Changed
        {
            add { _changed += value; Subscribers++; }
            remove { _changed -= value; Subscribers--; }
        }

        public void Refresh() => Refreshes++;
        public void Publish(CopilotUsageState next) { State = next; _changed?.Invoke(); }
    }

    private static void ClockDisposal()
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var source = new FakeUsageSource(Ready());
        var now = SeptemberNow;
        var model = new CopilotUsageModel(source, null, NoWarning, queue.Enqueue,
            () => now, () => China, clockInterval: TimeSpan.FromMilliseconds(10));
        var changes = 0;
        model.Changed += () => changes++;
        Check(SpinWait.SpinUntil(() => !queue.IsEmpty, TimeSpan.FromSeconds(3)));
        Thread.Sleep(50);
        Check(queue.Count == 1); // many ticks, one pending UI callback
        now = now.AddDays(1);
        Check(queue.TryDequeue(out var tick));
        tick!();
        Check(changes == 1 && model.Pace.ElapsedWorkdays == 9);
        Check(SpinWait.SpinUntil(() => !queue.IsEmpty, TimeSpan.FromSeconds(3)));
        model.Dispose();
        now = now.AddDays(1);
        while (queue.TryDequeue(out var callback)) callback();
        Thread.Sleep(50);
        while (queue.TryDequeue(out var callback)) callback();
        Check(changes == 1 && source.Subscribers == 0);
        Thread.Sleep(50);
        Check(queue.IsEmpty);
    }

    private static void Check(bool condition, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        _checks++;
        if (!condition)
            throw new InvalidOperationException($"Usage fixture assertion failed: {expression}");
    }
}

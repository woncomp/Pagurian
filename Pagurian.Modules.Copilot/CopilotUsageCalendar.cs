using System.Globalization;

namespace Pagurian.Modules.Copilot;

internal sealed record CopilotUsageDay(DateOnly Date, bool IsEligible, bool IsWorkday, bool IsToday);

internal sealed record CopilotUsageCycle(
    DateTimeOffset Start,
    DateTimeOffset Reset,
    bool IsEstimated,
    IReadOnlyList<CopilotUsageDay> Days)
{
    public IEnumerable<CopilotUsageDay> EligibleDays => Days.Where(day => day.IsEligible);
}

internal sealed record CopilotUsagePace(
    CopilotUsageCycle? Cycle,
    DateOnly Today,
    int TotalWorkdays,
    int ElapsedWorkdays,
    double? WorkdayPercentage,
    double? UsedPercentage,
    double? RawBalance,
    int? RoundedBalance,
    string? UnavailableReason,
    bool IsRestDay)
{
    public double TintWeight => RoundedBalance is { } days ? Math.Min(Math.Abs(days) / 6d, 1) : 0;
    public string BalanceText => RoundedBalance switch
    {
        null => UnavailableReason ?? "Comparison unavailable",
        0 => "Your credit usage is on track.",
        > 0 => $"You have a surplus of {RoundedBalance} {(RoundedBalance == 1 ? "day’s" : "days’")} worth of credits.",
        _ => $"You’re over budget by {-RoundedBalance} {(RoundedBalance == -1 ? "day’s" : "days’")} worth of credits.",
    };
    public string WorkdayLabel => ElapsedWorkdays == 0
        ? $"0 / {TotalWorkdays} workdays"
        : $"{CopilotUsageCalendar.Ordinal(ElapsedWorkdays)} day / {TotalWorkdays} workdays";
}

internal static class CopilotUsageCalendar
{
    // The SDK provides only reset. Subtract in the reset's original offset,
    // BEFORE conversion, rather than subtracting from a local wall clock.
    public static CopilotUsageCycle? Cycle(
        CopilotUsageQuota? quota, CopilotUsageSettings settings,
        DateTimeOffset now, TimeZoneInfo zone)
    {
        if (quota?.ResetDate is not { } reset || reset.Year <= 1)
            return null;
        var start = reset.AddMonths(-1);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reset, zone).DateTime);
        var eligible = new HashSet<DateOnly>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            if (TryLocalDayEnd(date, zone, out var end) && end >= start && end < reset)
                eligible.Add(date);
        }
        if (eligible.Count == 0)
            return new(start, reset, quota.ResetIsEstimated, []);
        first = eligible.Min();
        last = eligible.Max();
        first = first.AddDays(-(int)first.DayOfWeek);
        last = last.AddDays(6 - (int)last.DayOfWeek);
        var days = new List<CopilotUsageDay>();
        for (var date = first; date <= last; date = date.AddDays(1))
            days.Add(new(date, eligible.Contains(date), settings.IsWorkday(date), date == today));
        return new(start, reset, quota.ResetIsEstimated, days);
    }

    // Last valid local wall-clock tick owns the day. Ambiguous ends choose
    // the later instant. A DST gap at the end of a day must not push that
    // date into tomorrow; a wholly skipped civil date owns no instant.
    public static DateTimeOffset LocalDayEnd(DateOnly date, TimeZoneInfo zone)
    {
        if (TryLocalDayEnd(date, zone, out var end)) return end;
        throw new ArgumentException("The time zone skips this entire local date.", nameof(date));
    }

    private static bool TryLocalDayEnd(DateOnly date, TimeZoneInfo zone, out DateTimeOffset value)
    {
        var end = date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(end))
        {
            var invalid = end;
            do { end = end.AddHours(-1); } while (zone.IsInvalidTime(end));
            // Locate the last valid tick even when a historical transition is
            // not aligned to the hour/minute.
            long lo = end.Ticks, hi = invalid.Ticks;
            while (hi - lo > 1)
            {
                long middle = lo + (hi - lo) / 2;
                if (zone.IsInvalidTime(new DateTime(middle, DateTimeKind.Unspecified)))
                    hi = middle;
                else
                    lo = middle;
            }
            end = new DateTime(lo, DateTimeKind.Unspecified);
            if (DateOnly.FromDateTime(end) != date)
            {
                value = default;
                return false;
            }
        }
        var offset = zone.IsAmbiguousTime(end)
            ? zone.GetAmbiguousTimeOffsets(end).Min()
            : zone.GetUtcOffset(end);
        value = new DateTimeOffset(end, offset);
        return true;
    }

    public static CopilotUsagePace Calculate(
        CopilotUsageState state, CopilotUsageSettings settings,
        DateTimeOffset now, TimeZoneInfo zone)
    {
        var quota = state.Usage.PremiumInteractions;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var cycle = state.Status is CopilotUsageStatus.Unauthenticated or CopilotUsageStatus.Loading
            || state.IsLoggingIn ? null : Cycle(quota, settings, now, zone);
        var total = cycle?.EligibleDays.Count(day => day.IsWorkday) ?? 0;
        var elapsed = cycle?.EligibleDays.Count(day => day.IsWorkday && day.Date <= today) ?? 0;
        string? reason = null;
        if (cycle is null) reason = "Comparison unavailable";
        else if (now >= cycle.Reset || now < cycle.Start ||
                 (quota!.ReadStartedAt ?? state.RefreshedAt) is { } read && read < cycle.Start)
            reason = "Refreshing cycle — comparison unavailable";
        else if (state.Status != CopilotUsageStatus.Ready)
            reason = "Comparison unavailable";
        else if (total == 0) reason = "No workdays configured";
        else if (quota is not { IsUnlimited: false, UsedPercentage: { } percent } ||
                 !double.IsFinite(percent))
            reason = "Credits percentage unavailable";

        double? used = reason is null ? Math.Clamp(quota!.UsedPercentage!.Value, 0, 100) : null;
        double? balance = used is { } amount ? elapsed - amount / 100 * total : null;
        return new(cycle, today, total, elapsed,
            cycle is not null && total > 0 ? elapsed * 100d / total : null,
            used, balance,
            balance is { } rawBalance ? RoundBalance(rawBalance) : null,
            reason, !settings.IsWorkday(today));
    }

    public static int RoundBalance(double value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);

    public static string Ordinal(int value)
    {
        var suffix = value % 100 is 11 or 12 or 13 ? "th" : (value % 10) switch
        {
            1 => "st", 2 => "nd", 3 => "rd", _ => "th",
        };
        return value.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    // Labels move, never markers. Width is the measured label desired width
    // constrained to the card; track coordinates include the endpoint inset.
    public static double ClampLabel(double markerX, double labelWidth, double cardWidth) =>
        Math.Clamp(markerX - Math.Min(labelWidth, cardWidth) / 2, 0,
            Math.Max(0, cardWidth - labelWidth));

    public static double MarkerPosition(double percentage, double trackWidth, double inset) =>
        inset + Math.Max(0, trackWidth) * Math.Clamp(percentage, 0, 100) / 100;
}

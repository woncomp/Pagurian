namespace Pagurian.Modules.Copilot;

internal sealed record CopilotUsageHistoryCell(
    DateOnly Date, double? Used, double? PercentOfEntitlement, bool Estimated,
    double? StartUsed = null, bool StartEstimated = false);

internal sealed record CopilotUsageHistoryProjection(
    IReadOnlyList<CopilotUsageHistoryCell> Days, DateOnly Today, string? Error);

internal static class CopilotUsageHistoryCalculator
{
    internal const int DaysPerPage = 20;
    internal const int PageCount = 3;
    internal const int HistoryDays = DaysPerPage * PageCount;
    internal const long FallbackEntitlement = 75_000;

    internal static CopilotUsageHistoryProjection Calculate(
        CopilotUsageHistoryState history, CopilotUsageSettings settings,
        DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = today.AddDays(1 - HistoryDays);
        var values = new Dictionary<DateOnly, CopilotUsageHistoryCell>();

        foreach (var cycle in history.Cycles.OrderBy(cycle => cycle.Start))
        {
            var samples = cycle.Days.OrderBy(day => day.Date).ToArray();
            var entitlement = cycle.Entitlement ?? FallbackEntitlement;
            for (var i = 0; i < samples.Length; i++)
            {
                var sample = samples[i];
                if (sample.Date > today) continue;
                var next = i + 1 < samples.Length ? samples[i + 1] : null;
                var consecutive = next is not null &&
                    next.Date.DayNumber - sample.Date.DayNumber == 1 &&
                    next.FirstAt > sample.FirstAt && next.Date <= today;
                var ending = consecutive ? next!.FirstUsed : sample.LastUsed;
                if (ending >= sample.FirstUsed)
                    Add(values, sample.Date, ending - (double)sample.FirstUsed,
                        entitlement, estimated: false, sample.FirstUsed);
                else
                    values.TryAdd(sample.Date, new(sample.Date, null, null, false, sample.FirstUsed));

                if (next is null || next.Date > today ||
                    next.Date.DayNumber - sample.Date.DayNumber <= 1 ||
                    next.FirstAt <= sample.LastAt || next.FirstUsed < sample.LastUsed)
                    continue;

                var workdays = new List<DateOnly>();
                for (var number = sample.Date.DayNumber + 1; number < next.Date.DayNumber; number++)
                {
                    var date = DateOnly.FromDayNumber(number);
                    if (settings.IsWorkday(date))
                        workdays.Add(date);
                }
                if (workdays.Count == 0) continue;
                var amount = (next.FirstUsed - (double)sample.LastUsed) / workdays.Count;
                for (var day = 0; day < workdays.Count; day++)
                    Add(values, workdays[day], amount, entitlement, estimated: true,
                        sample.LastUsed + day * amount);
            }
        }

        var days = new List<CopilotUsageHistoryCell>(HistoryDays);
        for (var i = 0; i < HistoryDays; i++)
        {
            var date = first.AddDays(i);
            days.Add(values.TryGetValue(date, out var value)
                ? value
                : new(date, null, null, false));
        }
        return new(days, today, history.Error);
    }

    private static void Add(
        Dictionary<DateOnly, CopilotUsageHistoryCell> values,
        DateOnly date, double used, long entitlement, bool estimated, double startUsed)
    {
        var percentage = used / entitlement * 100;
        if (values.TryGetValue(date, out var current))
        {
            values[date] = current with
            {
                Used = current.Used.GetValueOrDefault() + used,
                PercentOfEntitlement = current.PercentOfEntitlement.GetValueOrDefault() + percentage,
                Estimated = current.Estimated || estimated,
            };
        }
        else
            values[date] = new(date, used, percentage, estimated, startUsed, estimated);
    }
}

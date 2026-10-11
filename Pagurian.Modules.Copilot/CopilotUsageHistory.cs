using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pagurian.Modules.Copilot;

internal sealed record CopilotUsageHistoryDay(
    DateOnly Date, DateTimeOffset FirstAt, long FirstUsed,
    DateTimeOffset LastAt, long LastUsed);

internal sealed record CopilotUsageHistoryCycle(
    DateTimeOffset Start, DateTimeOffset Reset, long? Entitlement,
    List<CopilotUsageHistoryDay> Days);

internal sealed record CopilotUsageHistoryState(
    IReadOnlyList<CopilotUsageHistoryCycle> Cycles, string? Error = null)
{
    public static CopilotUsageHistoryState Empty { get; } = new([]);
}

internal sealed class CopilotUsageHistoryStore
{
    private sealed record Document(int Version, List<CopilotUsageHistoryCycle> Cycles);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;

    internal CopilotUsageHistoryStore(string? directory = null) =>
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pagurian", "Copilot");

    internal string PathFor(CopilotUsageAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.Login) || string.IsNullOrWhiteSpace(account.Host))
            throw new InvalidDataException("The authenticated account has no login or host for usage history.");
        var identity = account.Host.Trim().ToUpperInvariant() + "\0" +
            account.Login.Trim().ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_directory, $"usage-history-{hash}.json");
    }

    internal CopilotUsageHistoryState Read(CopilotUsageAccount account)
    {
        var path = PathFor(account);
        if (!File.Exists(path))
            return CopilotUsageHistoryState.Empty;
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOptions);
        if (document is not { Version: 1, Cycles: not null })
            throw new InvalidDataException("The Copilot usage history file has an unsupported format.");
        Validate(document.Cycles);
        return new(document.Cycles);
    }

    internal CopilotUsageHistoryState Record(
        CopilotUsageAccount account, CopilotUsageQuota quota, DateTimeOffset sampledAt,
        TimeZoneInfo zone)
    {
        var path = PathFor(account);
        var history = Read(account);
        var reset = quota.ResetDate;
        if (reset is null || reset <= sampledAt || quota.UsedRequests < 0)
            throw new InvalidDataException("The quota has no valid current cycle for usage history.");
        var start = reset.Value.AddMonths(-1);
        if (sampledAt < start)
            throw new InvalidDataException("The quota predates its current usage cycle.");
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(sampledAt, zone).DateTime);
        var cycles = history.Cycles.ToList();
        var index = cycles.FindIndex(cycle => cycle.Reset == reset);
        var cycle = index < 0
            ? new CopilotUsageHistoryCycle(start, reset.Value, null, [])
            : cycles[index];
        if (cycle.Start != start)
            throw new InvalidDataException("The usage history contains conflicting cycle boundaries.");

        var days = cycle.Days.ToList();
        var dateIndex = days.FindIndex(entry => entry.Date == day);
        if (dateIndex < 0)
            days.Add(new(day, sampledAt, quota.UsedRequests, sampledAt, quota.UsedRequests));
        else
        {
            var existing = days[dateIndex];
            days[dateIndex] = sampledAt < existing.FirstAt
                ? existing with { FirstAt = sampledAt, FirstUsed = quota.UsedRequests }
                : sampledAt >= existing.LastAt
                    ? existing with { LastAt = sampledAt, LastUsed = quota.UsedRequests }
                    : existing;
        }

        var entitlement = quota.IsUnlimited || quota.EntitlementRequests <= 0
            ? cycle.Entitlement
            : quota.EntitlementRequests;
        var updated = cycle with { Entitlement = entitlement, Days = days };
        if (index < 0) cycles.Add(updated);
        else cycles[index] = updated;
        Validate(cycles);

        Directory.CreateDirectory(_directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(1, cycles), JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return new(cycles);
    }

    private static void Validate(List<CopilotUsageHistoryCycle> cycles)
    {
        var resets = new HashSet<DateTimeOffset>();
        foreach (var cycle in cycles)
        {
            if (cycle is null || cycle.Days is null ||
                (cycle.Reset.Year == 1 && cycle.Reset.Month == 1) ||
                cycle.Reset <= cycle.Start ||
                cycle.Start != cycle.Reset.AddMonths(-1) ||
                !resets.Add(cycle.Reset) ||
                cycle.Entitlement is <= 0)
                throw new InvalidDataException("The Copilot usage history contains an invalid cycle.");
            var dates = new HashSet<DateOnly>();
            foreach (var day in cycle.Days)
            {
                if (day is null || !dates.Add(day.Date) ||
                    day.FirstAt > day.LastAt || day.FirstAt < cycle.Start ||
                    day.LastAt >= cycle.Reset || day.FirstUsed < 0 || day.LastUsed < 0)
                    throw new InvalidDataException("The Copilot usage history contains an invalid sample.");
            }
        }
    }
}

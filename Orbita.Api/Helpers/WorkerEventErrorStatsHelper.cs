using Orbita.Contracts;

namespace Orbita.Api.Helpers;

internal static class WorkerEventErrorStatsHelper
{
    public const int DailyLookbackDays = 30;

    public static bool IsErrorLevel(string level) =>
        level.Equals("Error", StringComparison.OrdinalIgnoreCase)
        || level.Equals("Warning", StringComparison.OrdinalIgnoreCase);

    public static WorkerEventErrorStats Compute(
        IEnumerable<(Guid WorkerId, DateTime CreatedAtUtc)> events,
        DateTime todayStartUtc)
    {
        var hourly = new int[24];
        var daily = new Dictionary<DateTime, int>();
        var perWorkerToday = new Dictionary<Guid, int>();
        var todayCount = 0;

        foreach (var (workerId, createdAtUtc) in events)
        {
            var day = createdAtUtc.Date;
            daily[day] = daily.GetValueOrDefault(day) + 1;

            if (createdAtUtc < todayStartUtc)
                continue;

            todayCount++;
            hourly[createdAtUtc.Hour]++;
            perWorkerToday[workerId] = perWorkerToday.GetValueOrDefault(workerId) + 1;
        }

        return new WorkerEventErrorStats(todayCount, hourly, daily, perWorkerToday);
    }

    public static IReadOnlyList<ActivityPointDto> ApplyHourlyErrors(
        IReadOnlyList<ActivityPointDto> activity,
        IReadOnlyList<int> hourlyErrors)
    {
        if (hourlyErrors.Count != 24)
            return activity;

        return activity
            .Select(point => point.SlotStartHour is >= 0 and <= 23
                ? point with { ErrorCount = hourlyErrors[point.SlotStartHour] }
                : point)
            .ToList();
    }

    public static IReadOnlyList<ActivityPointDto> MergeDailyErrors(
        IReadOnlyList<ActivityPointDto> activity,
        IReadOnlyDictionary<DateTime, int> dailyErrors)
    {
        var map = activity
            .Where(point => point.LocalDate.HasValue)
            .ToDictionary(point => point.LocalDate!.Value.Date, point => point);

        foreach (var (date, errorCount) in dailyErrors)
        {
            if (map.TryGetValue(date, out var existing))
            {
                map[date] = existing with { ErrorCount = errorCount };
            }
            else
            {
                map[date] = new ActivityPointDto(
                    date.ToString("ddd d.MM"),
                    0,
                    0,
                    0,
                    errorCount,
                    0,
                    1,
                    date);
            }
        }

        return map.OrderBy(x => x.Key).Select(x => x.Value).ToList();
    }
}

internal sealed record WorkerEventErrorStats(
    int TodayCount,
    IReadOnlyList<int> Hourly,
    IReadOnlyDictionary<DateTime, int> Daily,
    IReadOnlyDictionary<Guid, int> PerWorkerToday)
{
    public static WorkerEventErrorStats Empty { get; } = new(
        0,
        Enumerable.Repeat(0, 24).ToArray(),
        new Dictionary<DateTime, int>(),
        new Dictionary<Guid, int>());
}
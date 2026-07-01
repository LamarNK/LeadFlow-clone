using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerEventErrorStatsHelperTests
{
    [Fact]
    public void Compute_CountsTodayHourlyDailyAndPerWorker()
    {
        var workerA = Guid.NewGuid();
        var workerB = Guid.NewGuid();
        var todayStart = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var stats = WorkerEventErrorStatsHelper.Compute(
        [
            (workerA, todayStart.AddHours(10)),
            (workerA, todayStart.AddHours(10).AddMinutes(5)),
            (workerB, todayStart.AddHours(15)),
            (workerA, todayStart.AddDays(-1).AddHours(9))
        ],
        todayStart);

        Assert.Equal(3, stats.TodayCount);
        Assert.Equal(2, stats.Hourly[10]);
        Assert.Equal(1, stats.Hourly[15]);
        Assert.Equal(2, stats.PerWorkerToday[workerA]);
        Assert.Equal(1, stats.PerWorkerToday[workerB]);
        Assert.Equal(3, stats.Daily[todayStart.Date]);
        Assert.Equal(1, stats.Daily[todayStart.AddDays(-1).Date]);
    }

    [Fact]
    public void ApplyHourlyErrors_ReplacesSnapshotErrorCounts()
    {
        var activity = Enumerable.Range(0, 24)
            .Select(hour => new ActivityPointDto($"{hour:00}:00", 5, 1, 0, 99, hour, 1, null))
            .ToList();

        var hourly = Enumerable.Repeat(0, 24).ToArray();
        hourly[12] = 4;

        var result = WorkerEventErrorStatsHelper.ApplyHourlyErrors(activity, hourly);

        Assert.Equal(0, result[11].ErrorCount);
        Assert.Equal(4, result[12].ErrorCount);
        Assert.Equal(5, result[12].NewCount);
    }

    [Fact]
    public void MergeDailyErrors_AddsMissingDaysAndOverridesCounts()
    {
        var date = new DateTime(2026, 7, 1);
        var activity = new List<ActivityPointDto>
        {
            new("Вт 1.07", 10, 2, 1, 99, 0, 1, date)
        };

        var result = WorkerEventErrorStatsHelper.MergeDailyErrors(
            activity,
            new Dictionary<DateTime, int>
            {
                [date] = 2,
                [date.AddDays(1)] = 5
            });

        Assert.Equal(2, result.Count);
        Assert.Equal(2, result.First(p => p.LocalDate == date).ErrorCount);
        Assert.Equal(5, result.First(p => p.LocalDate == date.AddDays(1)).ErrorCount);
        Assert.Equal(0, result.First(p => p.LocalDate == date.AddDays(1)).NewCount);
    }
}
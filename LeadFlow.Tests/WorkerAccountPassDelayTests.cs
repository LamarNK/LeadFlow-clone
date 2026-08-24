using LeadFlow.Core.Services;
using LeadFlow.Core.Services.Worker;
using Xunit;

namespace LeadFlow.Tests;

public sealed class WorkerAccountPassDelayTests
{
    [Fact]
    public void CdpRetry_NightQuiet_StaysOneMinute()
    {
        var night = new DateTime(2026, 8, 24, 20, 30, 0, DateTimeKind.Utc);
        Assert.True(MonitoringNightQuiet.IsActive(night));

        var delay = WorkerAccountPassDelay.Resolve(
            retryAfter: TimeSpan.FromMinutes(1),
            polled: true,
            newResponses: 0,
            quietStreak: 5,
            backlog: false,
            historicalHeat: 0,
            utcNow: night);

        Assert.Equal(TimeSpan.FromMinutes(1), delay);
    }

    [Fact]
    public void QuietPass_Night_RaisesToNightFloor()
    {
        var night = new DateTime(2026, 8, 24, 20, 30, 0, DateTimeKind.Utc);

        var delay = WorkerAccountPassDelay.Resolve(
            retryAfter: null,
            polled: true,
            newResponses: 0,
            quietStreak: 1,
            backlog: false,
            historicalHeat: 0,
            utcNow: night);

        Assert.True(delay.TotalMinutes >= MonitoringTiming.NightQuietDelayMinMinutes);
        Assert.True(delay.TotalMinutes <= MonitoringTiming.NightQuietDelayMaxMinutes);
    }

    [Fact]
    public void CdpRetry_Daytime_StaysOneMinute()
    {
        var day = new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc);
        Assert.False(MonitoringNightQuiet.IsActive(day));

        var delay = WorkerAccountPassDelay.Resolve(
            retryAfter: TimeSpan.FromMinutes(1),
            polled: true,
            newResponses: 0,
            quietStreak: 0,
            backlog: false,
            historicalHeat: 0,
            utcNow: day);

        Assert.Equal(TimeSpan.FromMinutes(1), delay);
    }
}

using Orbita.Api.Services;

namespace Orbita.Tests;

public class WorkerOnlineRulesTests
{
    [Fact]
    public void IsOnline_within_threshold()
    {
        var now = new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(WorkerOnlineRules.IsOnline(now.AddMinutes(-2), now));
        Assert.False(WorkerOnlineRules.IsOnline(now.AddMinutes(-10), now));
        Assert.False(WorkerOnlineRules.IsOnline(null, now));
    }

    [Fact]
    public void IsOnline_hub_connected_overrides_stale_last_seen()
    {
        var now = new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(WorkerOnlineRules.IsOnline(now.AddHours(-2), now, hubConnected: true));
    }
}
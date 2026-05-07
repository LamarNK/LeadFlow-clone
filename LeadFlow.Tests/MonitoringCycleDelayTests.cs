using LeadFlow.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class MonitoringCycleDelayTests
{
    [Fact]
    public void GetRandomDelay_StaysWithinConfiguredMinuteRange()
    {
        var rnd = new Random(42);
        for (var i = 0; i < 50; i++)
        {
            var d = MonitoringCycleDelay.GetRandomDelay(rnd);
            Assert.InRange(d.TotalMinutes, MonitoringTiming.CycleDelayMinMinutes, MonitoringTiming.CycleDelayMaxMinutes);
        }
    }
}

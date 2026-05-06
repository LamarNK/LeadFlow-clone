using LeadFlow.Models;
using LeadFlow.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class MonitoringCycleDelayTests
{
    [Fact]
    public void GetRandomDelay_SwapsWhenMinGreaterThanMax()
    {
        var o = new MonitoringSafetyOptions { CycleDelayMinMinutes = 5, CycleDelayMaxMinutes = 2 };
        var rnd = new Random(42);
        for (var i = 0; i < 20; i++)
        {
            var d = MonitoringCycleDelay.GetRandomDelay(o, rnd);
            Assert.InRange(d.TotalMinutes, 2, 5);
        }
    }

    [Fact]
    public void NormalizeBounds_SwapsAndClamps()
    {
        var o = new MonitoringSafetyOptions { CycleDelayMinMinutes = 200, CycleDelayMaxMinutes = 0 };
        MonitoringCycleDelay.NormalizeBounds(o);
        Assert.Equal(1, o.CycleDelayMinMinutes);
        Assert.Equal(MonitoringCycleDelay.MaxAllowedMinutes, o.CycleDelayMaxMinutes);
    }

    [Fact]
    public void GetRandomDelay_ClampsOutOfRangeWithoutMutatingOptions()
    {
        var o = new MonitoringSafetyOptions { CycleDelayMinMinutes = 0, CycleDelayMaxMinutes = 500 };
        var d = MonitoringCycleDelay.GetRandomDelay(o, new Random(1));
        Assert.InRange(d.TotalMinutes, MonitoringCycleDelay.MinAllowedMinutes, MonitoringCycleDelay.MaxAllowedMinutes);
        Assert.Equal(0, o.CycleDelayMinMinutes);
        Assert.Equal(500, o.CycleDelayMaxMinutes);
    }
}

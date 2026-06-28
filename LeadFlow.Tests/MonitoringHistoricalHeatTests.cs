using LeadFlow.Core.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class MonitoringHistoricalHeatTests
{
    [Fact]
    public void ComputeScore_BelowMinSamples_ReturnsZero()
    {
        var samples = Enumerable.Repeat(new DateTime(2026, 5, 4, 10, 0, 0, DateTimeKind.Utc), 10).ToList();
        var score = MonitoringHistoricalHeat.ComputeScore(samples, new DateTime(2026, 5, 7, 10, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc);
        Assert.Equal(0, score);
    }

    [Fact]
    public void ComputeScore_SingleHotSlot_NormalizesToOne()
    {
        var samples = new List<DateTime>();
        for (var i = 0; i < 30; i++)
        {
            samples.Add(new DateTime(2026, 5, 4, 14, 0, 0, DateTimeKind.Utc));
        }

        var utcNow = new DateTime(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc);
        var score = MonitoringHistoricalHeat.ComputeScore(samples, utcNow, TimeZoneInfo.Utc);
        Assert.InRange(score, 0.99, 1.01);
    }

    [Fact]
    public void ComputeScore_SmoothingUsesNeighborHours()
    {
        var samples = new List<DateTime>();
        for (var i = 0; i < 30; i++)
        {
            samples.Add(new DateTime(2026, 5, 4, 15, 0, 0, DateTimeKind.Utc));
        }

        var atNeighbor = new DateTime(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc);
        var score = MonitoringHistoricalHeat.ComputeScore(samples, atNeighbor, TimeZoneInfo.Utc);
        Assert.InRange(score, 0.45, 0.55);
    }
}

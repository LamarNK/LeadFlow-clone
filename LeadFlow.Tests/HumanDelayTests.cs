using System.Diagnostics;
using LeadFlow.Core.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class HumanDelayTests
{
    [Fact]
    public async Task DelayAsync_ZeroBounds_ReturnsImmediately()
    {
        var sw = Stopwatch.StartNew();
        await HumanDelay.DelayAsync(0, 0);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50,
            $"Expected near-zero delay, got {sw.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task DelayAsync_RangeIsRespected_LowerBound()
    {
        var sw = Stopwatch.StartNew();
        await HumanDelay.DelayAsync(80, 120);
        sw.Stop();

        // Нижняя граница: должно быть не меньше 80 мс с поправкой на разрешение таймера.
        Assert.True(sw.ElapsedMilliseconds >= 60,
            $"Expected >= 60 ms (target lower bound 80 ms), got {sw.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task DelayAsync_HandlesSwappedBounds()
    {
        // Если случайно передали max < min — функция не должна падать.
        var sw = Stopwatch.StartNew();
        await HumanDelay.DelayAsync(120, 80);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 60,
            $"Expected >= 60 ms even with swapped bounds, got {sw.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task DelayAsync_Cancellation_PropagatesAsOperationCanceled()
    {
        using var cts = new CancellationTokenSource(20);
        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await HumanDelay.DelayAsync(2_000, 3_000, cts.Token));
    }

    [Fact]
    public void CandidateClickDelayBounds_AreOrdered()
    {
        Assert.True(MonitoringTiming.HumanDelayBeforeCandidateClickMinMs
            <= MonitoringTiming.HumanDelayBeforeCandidateClickMaxMs);
        Assert.True(MonitoringTiming.HumanDelayAfterCandidateClickMinMs
            <= MonitoringTiming.HumanDelayAfterCandidateClickMaxMs);
        Assert.True(MonitoringTiming.HumanDelayAfterDetailPanelReadMinMs
            <= MonitoringTiming.HumanDelayAfterDetailPanelReadMaxMs);
        Assert.True(MonitoringTiming.HumanDelayAfterListScrollMinMs
            <= MonitoringTiming.HumanDelayAfterListScrollMaxMs);
        Assert.True(MonitoringTiming.HumanDelayAfterPhoneRevealClickMinMs
            <= MonitoringTiming.HumanDelayAfterPhoneRevealClickMaxMs);
        Assert.True(MonitoringTiming.HumanDelayAfterPhoneRevealSuccessMinMs
            <= MonitoringTiming.HumanDelayAfterPhoneRevealSuccessMaxMs);
        Assert.True(MonitoringTiming.HumanDelayAfterPhoneRevealMissMinMs
            <= MonitoringTiming.HumanDelayAfterPhoneRevealMissMaxMs);
        Assert.True(MonitoringTiming.HumanDelayAfterMessengerCardMinMs
            <= MonitoringTiming.HumanDelayAfterMessengerCardMaxMs);
        Assert.True(MonitoringTiming.HumanTypeCharDelayMinMs
            <= MonitoringTiming.HumanTypeCharDelayMaxMs);
        Assert.True(MonitoringTiming.ContactsPopupPollMinMs
            <= MonitoringTiming.ContactsPopupPollMaxMs);
        Assert.True(MonitoringTiming.HumanTypeCharDelayMinMs >= 30);
        Assert.True(MonitoringTiming.HumanDelayAfterMessengerCardMinMs >= 2000);
        Assert.InRange(MonitoringTiming.MaxPhoneRevealsPerSubProfilePerCycle, 8, 12);
        Assert.True(MonitoringTiming.MinPhoneRevealsPerSubProfilePerCycle
                    <= MonitoringTiming.MaxPhoneRevealsPerSubProfilePerCycle);
        Assert.InRange(MonitoringTiming.MaxMessengerAutoRepliesPerSubProfilePerCycle, 2, 3);
        Assert.True(MonitoringTiming.HumanDelayAfterListReadyMinMs
                    <= MonitoringTiming.HumanDelayAfterListReadyMaxMs);
    }

    [Fact]
    public void NextTypeCharDelayMs_StaysInsideBounds()
    {
        for (var i = 0; i < 40; i++)
        {
            var delay = HumanDelay.NextTypeCharDelayMs();
            Assert.InRange(
                delay,
                MonitoringTiming.HumanTypeCharDelayMinMs,
                MonitoringTiming.HumanTypeCharDelayMaxMs);
        }
    }

    [Fact]
    public async Task BeforeCandidateClickAsync_RespectsLowerBound()
    {
        var sw = Stopwatch.StartNew();
        await HumanDelay.BeforeCandidateClickAsync();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= MonitoringTiming.HumanDelayBeforeCandidateClickMinMs - 80,
            $"Expected >= {MonitoringTiming.HumanDelayBeforeCandidateClickMinMs - 80} ms, got {sw.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task DelayAsync_ProducesVariation_OverManyRuns()
    {
        // Несколько прогонов — должны давать разные значения (рандом, а не константа).
        var samples = new HashSet<long>();
        for (var i = 0; i < 8; i++)
        {
            var sw = Stopwatch.StartNew();
            await HumanDelay.DelayAsync(20, 80);
            sw.Stop();
            samples.Add(sw.ElapsedMilliseconds / 5); // bucket по 5 мс, чтобы шум таймера не мешал
        }

        // На 8 запусках хотя бы 2 разных бакета — ничтожный шанс ложного провала.
        Assert.True(samples.Count >= 2,
            $"Expected at least 2 distinct buckets across 8 runs, got {samples.Count}.");
    }
}

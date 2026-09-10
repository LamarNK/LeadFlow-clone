using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Worker;
using Xunit;

namespace LeadFlow.Tests;

public sealed class CandidatesIncrementalScrollStateTests
{
    [Fact]
    public void NewCards_AreCountedExactlyOncePerIncrementalPackage()
    {
        var state = new CandidatesIncrementalScrollState([]);

        state.ObserveNames(["Первый", "Второй"]);
        state.ObserveNames(["Третий"]);

        Assert.Equal(3, state.ParsedItems);
    }

    [Fact]
    public void KnownHistory_StopsNormalPassAfterTwoKnownOnlyLoadsFollowingUnknown()
    {
        var state = new CandidatesIncrementalScrollState([]);

        Assert.False(state.ObserveKnownHistory(allKnown: false, allowEarlyStop: true));
        Assert.False(state.ObserveKnownHistory(allKnown: true, allowEarlyStop: true));
        Assert.True(state.ObserveKnownHistory(allKnown: true, allowEarlyStop: true));
    }

    [Fact]
    public void ActivePhoneWatch_PreventsStopUntilNameIsFound()
    {
        var watchedName = ResponsePhoneWatchEvaluator.BuildFullNameKey("Иван Иванов");
        var state = new CandidatesIncrementalScrollState([watchedName]);

        state.ObserveKnownHistory(allKnown: false, allowEarlyStop: true);
        Assert.False(state.ObserveKnownHistory(allKnown: true, allowEarlyStop: true));
        Assert.False(state.ObserveKnownHistory(allKnown: true, allowEarlyStop: true));

        state.ObserveNames(["Иван Иванов"]);

        Assert.False(state.HasRemainingPhoneWatch);
        Assert.True(state.ObserveKnownHistory(allKnown: true, allowEarlyStop: true));
    }

    [Fact]
    public void FullRescanRound_NeverAllowsEarlyStop()
    {
        var state = new CandidatesIncrementalScrollState([]);
        state.ObserveKnownHistory(allKnown: false, allowEarlyStop: true);
        state.ObserveKnownHistory(allKnown: true, allowEarlyStop: true);

        Assert.False(state.ObserveKnownHistory(allKnown: true, allowEarlyStop: false));
    }
}

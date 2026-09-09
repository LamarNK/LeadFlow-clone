using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class CandidatesScrollStopTests
{
    [Fact]
    public void Stops_AfterUnknownsThenTwoKnownOnlyLoads() =>
        Assert.True(CandidatesScrollStop.ShouldStopAfterKnownHistory(seenUnknownCard: true, consecutiveKnownOnlyLoadRounds: 2));

    [Fact]
    public void DoesNotStop_OnKnownPrefixBeforeAnyUnknown() =>
        Assert.False(CandidatesScrollStop.ShouldStopAfterKnownHistory(seenUnknownCard: false, consecutiveKnownOnlyLoadRounds: 5));

    [Fact]
    public void DoesNotStop_WhileStillLoadingUnknowns() =>
        Assert.False(CandidatesScrollStop.ShouldStopAfterKnownHistory(seenUnknownCard: true, consecutiveKnownOnlyLoadRounds: 1));

    [Fact]
    public void DoesNotStop_WhenNothingLoaded() =>
        Assert.False(CandidatesScrollStop.ShouldStopAfterKnownHistory(seenUnknownCard: false, consecutiveKnownOnlyLoadRounds: 0));

    [Fact]
    public void DoesNotStop_WhileSubProfileHasOpenPhoneWatches() =>
        Assert.False(CandidatesScrollStop.ShouldStopAfterKnownHistory(
            seenUnknownCard: true,
            consecutiveKnownOnlyLoadRounds: 2,
            hasOpenPhoneWatches: true));
}

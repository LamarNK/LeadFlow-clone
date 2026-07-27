using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;
using Xunit;

namespace LeadFlow.Tests;

public sealed class ResponsePhoneWatchEvaluatorTests
{
    private const string Sub = "sub-1";
    private const string Name = "иванов иван иванович";

    [Fact]
    public void FirstSight_OnlyRemembers_DoesNotPublish()
    {
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var decision = ResponsePhoneWatchEvaluator.Evaluate(
            existing: null,
            Sub,
            Name,
            phoneRaw: "+7 900 111-22-33",
            phoneNormalized: "79001112233",
            phoneUnchangedHours: 24,
            utcNow: now);

        Assert.Equal(ResponsePhoneWatchAction.Skip, decision.Action);
        Assert.Equal("79001112233", decision.NextObservation.PhoneNormalized);
        Assert.Equal(string.Empty, decision.NextObservation.LastPublishedPhoneNormalized);
        Assert.False(decision.NextObservation.ClosedAfterStableSend);
        Assert.Equal(Sub, decision.NextObservation.AvitoSubProfileId);
        Assert.Equal(Name, decision.NextObservation.FullNameKey);
    }

    [Fact]
    public void SamePhone_BelowThreshold_Skips()
    {
        var firstSeen = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
        var now = firstSeen.AddHours(12);
        var existing = Seed(firstSeen, "79001112233");

        var decision = ResponsePhoneWatchEvaluator.Evaluate(
            existing,
            Sub,
            Name,
            phoneRaw: "+7 900 111-22-33",
            phoneNormalized: "79001112233",
            phoneUnchangedHours: 24,
            utcNow: now);

        Assert.Equal(ResponsePhoneWatchAction.Skip, decision.Action);
        Assert.False(decision.NextObservation.ClosedAfterStableSend);
    }

    [Fact]
    public void SamePhone_AboveThreshold_PublishesUnchangedAndCloses()
    {
        var firstSeen = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
        var now = firstSeen.AddHours(25);
        var existing = Seed(firstSeen, "79001112233");

        var decision = ResponsePhoneWatchEvaluator.Evaluate(
            existing,
            Sub,
            Name,
            phoneRaw: "+7 900 111-22-33",
            phoneNormalized: "79001112233",
            phoneUnchangedHours: 24,
            utcNow: now);

        Assert.Equal(ResponsePhoneWatchAction.PublishPhoneUnchanged, decision.Action);
        Assert.True(decision.UnchangedHours >= 24);
        Assert.True(decision.NextObservation.ClosedAfterStableSend);
        Assert.Equal(ResponsePhoneMetricKinds.PhoneUnchanged, decision.NextObservation.LastPublishedMetricKind);
    }

    [Fact]
    public void PhoneChanged_PublishesChangedAndResetsTimer()
    {
        var firstSeen = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
        var now = firstSeen.AddHours(5);
        var existing = Seed(firstSeen, "79001112233", phoneRaw: "+7 900 111-22-33");

        var decision = ResponsePhoneWatchEvaluator.Evaluate(
            existing,
            Sub,
            Name,
            phoneRaw: "+7 900 999-88-77",
            phoneNormalized: "79009998877",
            phoneUnchangedHours: 24,
            utcNow: now);

        Assert.Equal(ResponsePhoneWatchAction.PublishPhoneChanged, decision.Action);
        Assert.Equal("79001112233", decision.PreviousPhoneNormalized);
        Assert.Equal("79009998877", decision.NextObservation.PhoneNormalized);
        Assert.Equal(now, decision.NextObservation.PhoneFirstSeenUtc);
        Assert.False(decision.NextObservation.ClosedAfterStableSend);
    }

    [Fact]
    public void Closed_AlwaysSkipsEvenIfPhoneChanges()
    {
        var firstSeen = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
        var now = firstSeen.AddHours(50);
        var existing = Seed(firstSeen, "79001112233");
        existing.ClosedAfterStableSend = true;

        var decision = ResponsePhoneWatchEvaluator.Evaluate(
            existing,
            Sub,
            Name,
            phoneRaw: "+7 900 999-88-77",
            phoneNormalized: "79009998877",
            phoneUnchangedHours: 24,
            utcNow: now);

        Assert.Equal(ResponsePhoneWatchAction.Skip, decision.Action);
        Assert.True(decision.NextObservation.ClosedAfterStableSend);
    }

    [Fact]
    public void ThresholdZero_NeverPublishesUnchanged()
    {
        var firstSeen = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
        var now = firstSeen.AddHours(100);
        var existing = Seed(firstSeen, "79001112233");

        var decision = ResponsePhoneWatchEvaluator.Evaluate(
            existing,
            Sub,
            Name,
            phoneRaw: "+7 900 111-22-33",
            phoneNormalized: "79001112233",
            phoneUnchangedHours: 0,
            utcNow: now);

        Assert.Equal(ResponsePhoneWatchAction.Skip, decision.Action);
        Assert.False(decision.NextObservation.ClosedAfterStableSend);
    }

    [Fact]
    public void BuildFullNameKey_NormalizesCaseAndSpaces()
    {
        var key = ResponsePhoneWatchEvaluator.BuildFullNameKey("  Иванов   Иван  Иванович ");
        Assert.Equal("иванов иван иванович", key);
    }

    private static ResponsePhoneObservation Seed(DateTime firstSeen, string phone, string? phoneRaw = null) =>
        new()
        {
            AvitoSubProfileId = Sub,
            FullNameKey = Name,
            PhoneRaw = phoneRaw ?? phone,
            PhoneNormalized = phone,
            PhoneFirstSeenUtc = firstSeen,
            LastSeenUtc = firstSeen,
            ClosedAfterStableSend = false,
            LastPublishedPhoneNormalized = string.Empty,
            LastPublishedMetricKind = ResponsePhoneMetricKinds.None
        };
}

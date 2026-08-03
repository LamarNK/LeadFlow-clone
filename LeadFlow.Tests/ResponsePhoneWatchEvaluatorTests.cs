using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;
using Xunit;

namespace LeadFlow.Tests;

public sealed class ResponsePhoneWatchEvaluatorTests
{
    private const string Sub = "sub-1";
    private const string Name = "иванов иван иванович";
    private const int WatchHours = 120; // 5 days

    [Fact]
    public void FirstSight_PublishesImmediately_OpensWatch()
    {
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var decision = ResponsePhoneWatchEvaluator.Evaluate(
            existing: null,
            Sub,
            Name,
            phoneRaw: "+7 900 111-22-33",
            phoneNormalized: "79001112233",
            phoneWatchHours: WatchHours,
            utcNow: now);

        Assert.Equal(ResponsePhoneWatchAction.PublishInitial, decision.Action);
        Assert.Equal("79001112233", decision.NextObservation.PhoneNormalized);
        Assert.Equal("79001112233", decision.NextObservation.LastPublishedPhoneNormalized);
        Assert.False(decision.NextObservation.ClosedAfterStableSend);
        Assert.Equal(now, decision.NextObservation.WatchStartedUtc);
        Assert.False(string.IsNullOrWhiteSpace(decision.NextObservation.PublishedSourceResponseId));
        Assert.StartsWith("phone-watch:", decision.NextObservation.PublishedSourceResponseId);
        Assert.Equal(Sub, decision.NextObservation.AvitoSubProfileId);
        Assert.Equal(Name, decision.NextObservation.FullNameKey);
    }

    [Fact]
    public void SamePhone_WithinWindow_Skips()
    {
        var firstSeen = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
        var now = firstSeen.AddHours(12);
        var existing = SeedPublished(firstSeen, "79001112233");

        var decision = ResponsePhoneWatchEvaluator.Evaluate(
            existing,
            Sub,
            Name,
            phoneRaw: "+7 900 111-22-33",
            phoneNormalized: "79001112233",
            phoneWatchHours: WatchHours,
            utcNow: now);

        Assert.Equal(ResponsePhoneWatchAction.Skip, decision.Action);
        Assert.False(decision.NextObservation.ClosedAfterStableSend);
        Assert.Equal(existing.PublishedSourceResponseId, decision.NextObservation.PublishedSourceResponseId);
    }

    [Fact]
    public void PhoneChanged_WithinWindow_PublishesChanged_SameSourceId()
    {
        var firstSeen = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
        var now = firstSeen.AddHours(5);
        var existing = SeedPublished(firstSeen, "79001112233", phoneRaw: "+7 900 111-22-33");
        var sourceId = existing.PublishedSourceResponseId;

        var decision = ResponsePhoneWatchEvaluator.Evaluate(
            existing,
            Sub,
            Name,
            phoneRaw: "+7 900 999-88-77",
            phoneNormalized: "79009998877",
            phoneWatchHours: WatchHours,
            utcNow: now);

        Assert.Equal(ResponsePhoneWatchAction.PublishPhoneChanged, decision.Action);
        Assert.Equal("79001112233", decision.PreviousPhoneNormalized);
        Assert.Equal("79009998877", decision.NextObservation.PhoneNormalized);
        Assert.Equal(sourceId, decision.NextObservation.PublishedSourceResponseId);
        Assert.Equal(now, decision.NextObservation.PhoneFirstSeenUtc);
        Assert.False(decision.NextObservation.ClosedAfterStableSend);
        Assert.Equal(ResponsePhoneMetricKinds.PhoneChanged, decision.NextObservation.LastPublishedMetricKind);
    }

    [Fact]
    public void AfterWatchWindow_ClosesAndSkipsEvenIfPhoneChanges()
    {
        var firstSeen = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
        var now = firstSeen.AddHours(WatchHours + 1);
        var existing = SeedPublished(firstSeen, "79001112233");

        var decision = ResponsePhoneWatchEvaluator.Evaluate(
            existing,
            Sub,
            Name,
            phoneRaw: "+7 900 999-88-77",
            phoneNormalized: "79009998877",
            phoneWatchHours: WatchHours,
            utcNow: now);

        Assert.Equal(ResponsePhoneWatchAction.Skip, decision.Action);
        Assert.True(decision.NextObservation.ClosedAfterStableSend);
    }

    [Fact]
    public void Closed_AlwaysSkipsEvenIfPhoneChanges()
    {
        var firstSeen = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
        var now = firstSeen.AddHours(50);
        var existing = SeedPublished(firstSeen, "79001112233");
        existing.ClosedAfterStableSend = true;

        var decision = ResponsePhoneWatchEvaluator.Evaluate(
            existing,
            Sub,
            Name,
            phoneRaw: "+7 900 999-88-77",
            phoneNormalized: "79009998877",
            phoneWatchHours: WatchHours,
            utcNow: now);

        Assert.Equal(ResponsePhoneWatchAction.Skip, decision.Action);
        Assert.True(decision.NextObservation.ClosedAfterStableSend);
    }

    [Fact]
    public void WatchHoursZero_PublishesInitialAndCloses()
    {
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var decision = ResponsePhoneWatchEvaluator.Evaluate(
            existing: null,
            Sub,
            Name,
            phoneRaw: "+7 900 111-22-33",
            phoneNormalized: "79001112233",
            phoneWatchHours: 0,
            utcNow: now);

        Assert.Equal(ResponsePhoneWatchAction.PublishInitial, decision.Action);
        Assert.True(decision.NextObservation.ClosedAfterStableSend);
    }

    [Fact]
    public void BuildFullNameKey_NormalizesCaseAndSpaces()
    {
        var key = ResponsePhoneWatchEvaluator.BuildFullNameKey("  Иванов   Иван  Иванович ");
        Assert.Equal("иванов иван иванович", key);
    }

    [Fact]
    public void BuildPublishedSourceResponseId_IsStableAcrossPhones()
    {
        var a = ResponsePhoneWatchEvaluator.BuildPublishedSourceResponseId(Sub, Name);
        var b = ResponsePhoneWatchEvaluator.BuildPublishedSourceResponseId(Sub, Name);
        Assert.Equal(a, b);
        Assert.StartsWith("phone-watch:", a);
    }

    private static ResponsePhoneObservation SeedPublished(
        DateTime firstSeen,
        string phone,
        string? phoneRaw = null) =>
        new()
        {
            AvitoSubProfileId = Sub,
            FullNameKey = Name,
            PhoneRaw = phoneRaw ?? phone,
            PhoneNormalized = phone,
            PhoneFirstSeenUtc = firstSeen,
            LastSeenUtc = firstSeen,
            WatchStartedUtc = firstSeen,
            ClosedAfterStableSend = false,
            LastPublishedPhoneNormalized = phone,
            LastPublishedMetricKind = ResponsePhoneMetricKinds.None,
            PublishedSourceResponseId = ResponsePhoneWatchEvaluator.BuildPublishedSourceResponseId(Sub, Name)
        };
}

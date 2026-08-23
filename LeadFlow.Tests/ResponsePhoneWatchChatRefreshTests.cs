using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;
using Xunit;

namespace LeadFlow.Tests;

public sealed class ResponsePhoneWatchChatRefreshTests
{
    [Fact]
    public void ShouldPublish_WhenWatchOpen_Skip_AndChatPresent()
    {
        Assert.True(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.Skip,
            chatMessagesJson: """[{"text":"привет","side":"left"}]"""));
    }

    [Fact]
    public void ShouldNotPublish_WhenWatchClosed()
    {
        Assert.False(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: false,
            ResponsePhoneWatchAction.Skip,
            chatMessagesJson: """[{"text":"привет","side":"left"}]"""));
    }

    [Fact]
    public void ShouldNotPublish_WhenChatEmpty()
    {
        Assert.False(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.Skip,
            chatMessagesJson: ""));
        Assert.False(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.Skip,
            chatMessagesJson: null));
        Assert.False(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.Skip,
            chatMessagesJson: "   "));
    }

    [Fact]
    public void ShouldPublish_WhenWatchOpen_Skip_AndProfileFieldsPresent()
    {
        Assert.True(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.Skip,
            chatMessagesJson: "",
            hasProfileRefresh: true));
        Assert.True(ResponsePhoneWatchChatRefresh.HasProfileRefresh(new CandidateResponse
        {
            City = "Батайск",
            Vacancy = "Разнорабочий вахта"
        }));
        Assert.False(ResponsePhoneWatchChatRefresh.HasProfileRefresh(new CandidateResponse()));
    }

    [Fact]
    public void ShouldNotPublish_WhenActionIsNotSkip()
    {
        Assert.False(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.PublishInitial,
            chatMessagesJson: """[{"text":"привет"}]"""));
        Assert.False(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.PublishPhoneChanged,
            chatMessagesJson: """[{"text":"привет"}]"""));
    }

    [Fact]
    public void RestoreFromOrbita_RecreatesOpenWatchFromStoredPhoneWatch()
    {
        var now = new DateTime(2026, 8, 23, 10, 0, 0, DateTimeKind.Utc);
        var stored = new WorkerKnownSourceResponseDto(
            "phone-watch:1a2b3c4d",
            now.AddHours(-4),
            "+7 933 401-04-97",
            "79334010497");

        var restored = ResponsePhoneWatchOrbitaState.RestoreObservation(
            stored,
            "sub-1",
            "автономов никита андреевич",
            phoneWatchHours: 120,
            now);

        Assert.NotNull(restored);
        Assert.False(restored!.ClosedAfterStableSend);
        Assert.Equal("79334010497", restored.LastPublishedPhoneNormalized);
        Assert.Equal("phone-watch:1a2b3c4d", restored.PublishedSourceResponseId);
    }

    [Fact]
    public void OrderByOrbitaAddedAt_PrioritizesNewestKnownPhoneWatch()
    {
        var newer = new CandidateResponse { FullName = "Новый Кандидат", AvitoSubProfileId = "sub-1" };
        var older = new CandidateResponse { FullName = "Старый Кандидат", AvitoSubProfileId = "sub-1" };
        var now = new DateTime(2026, 8, 23, 10, 0, 0, DateTimeKind.Utc);
        var known = new[]
        {
            new WorkerKnownSourceResponseDto(
                ResponsePhoneWatchEvaluator.BuildPublishedSourceResponseId("sub-1", "старый кандидат"),
                now.AddDays(-2), "", ""),
            new WorkerKnownSourceResponseDto(
                ResponsePhoneWatchEvaluator.BuildPublishedSourceResponseId("sub-1", "новый кандидат"),
                now.AddHours(-2), "", "")
        };

        var ordered = ResponsePhoneWatchOrbitaState.OrderByAddedAt([older, newer], known);

        Assert.Same(newer, ordered[0]);
        Assert.Same(older, ordered[1]);
    }
}

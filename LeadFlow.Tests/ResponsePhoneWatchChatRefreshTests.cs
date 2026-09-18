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
    public void HasPayloadChanged_ReturnsFalseForSameStoredFingerprints()
    {
        var candidate = new CandidateResponse
        {
            City = "Самара",
            Vacancy = "Охранник",
            Age = 35,
            Gender = CandidateGenders.Male,
            VacancyUrl = "https://www.avito.ru/1",
            MessengerUrl = "https://www.avito.ru/messenger",
            ChatMessagesJson = """[{"text":"привет"}]"""
        };
        var stored = new WorkerKnownSourceResponseDto(
            "phone-watch:1a2b3c4d",
            DateTime.UtcNow,
            "+79001111111",
            "79001111111",
            CandidateWatchFingerprint.Profile(
                candidate.City,
                candidate.Vacancy,
                candidate.Age,
                candidate.Gender,
                candidate.VacancyUrl,
                candidate.Citizenship,
                candidate.MessengerUrl),
            CandidateWatchFingerprint.Chat(candidate.ChatMessagesJson));

        Assert.False(ResponsePhoneWatchChatRefresh.HasPayloadChanged(candidate, stored));

        candidate.City = "Тольятти";
        Assert.True(ResponsePhoneWatchChatRefresh.HasPayloadChanged(candidate, stored));
    }

    [Fact]
    public void HasResponseDateRefresh_IsTrueWhenCardDatePrecedesCollection()
    {
        var candidate = new CandidateResponse
        {
            CreatedAt = new DateTime(2026, 9, 15, 6, 41, 20, DateTimeKind.Utc),
            CollectedAt = new DateTime(2026, 9, 15, 7, 55, 23, DateTimeKind.Utc)
        };

        Assert.True(ResponsePhoneWatchChatRefresh.HasResponseDateRefresh(candidate));
    }

    [Fact]
    public void HasResponseDateRefresh_IsFalseForCollectionFallback()
    {
        var collected = new DateTime(2026, 9, 15, 7, 55, 23, DateTimeKind.Utc);
        var candidate = new CandidateResponse { CreatedAt = collected, CollectedAt = collected };

        Assert.False(ResponsePhoneWatchChatRefresh.HasResponseDateRefresh(candidate));
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
    public void RestoreFromOrbita_ClosesWatch_WhenDealClosedInCrm()
    {
        var now = new DateTime(2026, 8, 23, 10, 0, 0, DateTimeKind.Utc);
        var stored = new WorkerKnownSourceResponseDto(
            "phone-watch:1a2b3c4d",
            now.AddHours(-4),
            "+7 933 401-04-97",
            "79334010497",
            WatchClosedInCrm: true);

        var restored = ResponsePhoneWatchOrbitaState.RestoreObservation(
            stored,
            "sub-1",
            "автономов никита андреевич",
            phoneWatchHours: 120,
            now);

        Assert.NotNull(restored);
        Assert.True(restored!.ClosedAfterStableSend);
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

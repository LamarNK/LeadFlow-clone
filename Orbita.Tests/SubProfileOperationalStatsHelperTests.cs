using Orbita.Api.Helpers;

namespace Orbita.Tests;

public sealed class SubProfileOperationalStatsHelperTests
{
    private static readonly Guid AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public void ApplyEvents_UpdatesLastActivity_FromJournalEvent()
    {
        var stats = new Dictionary<(Guid, string), SubProfileOperationalStats>();
        var todayStart = DateTime.UtcNow.Date;
        var eventAt = todayStart.AddHours(3);
        var lookup = new Dictionary<Guid, IReadOnlyDictionary<string, string>>
        {
            [AccountId] = new Dictionary<string, string>
            {
                ["sp-1"] = "Основной"
            }
        };

        SubProfileOperationalStatsHelper.ApplyEvents(
            stats,
            [(AccountId, """{"subProfileId":"sp-1"}""", eventAt, "Warning")],
            lookup,
            todayStart);

        Assert.True(stats.TryGetValue((AccountId, "sp-1"), out var operational));
        Assert.Equal(1, operational.TodayEventErrors);
        Assert.Equal(eventAt, operational.LastActivityUtc);
    }

    [Fact]
    public void Enrich_UsesLatestIssueAt_WhenNoOperationalActivity()
    {
        var issueAt = DateTime.UtcNow.AddHours(-1);
        var profiles = new[]
        {
            new Orbita.Contracts.WorkerSubProfileDto(
                "sp-1",
                "Основной",
                "Работа",
                true,
                1000m,
                null,
                null,
                issueAt)
        };

        var enriched = SubProfileOperationalStatsHelper.Enrich(
            profiles,
            AccountId,
            new Dictionary<(Guid, string), SubProfileOperationalStats>());

        Assert.Equal(issueAt, Assert.Single(enriched!).LastActivityUtc);
    }

    [Fact]
    public void Enrich_PrefersJournalActivity_OverIssueAt()
    {
        var issueAt = DateTime.UtcNow.AddDays(-1);
        var eventAt = DateTime.UtcNow.AddHours(-2);
        var profiles = new[]
        {
            new Orbita.Contracts.WorkerSubProfileDto(
                "sp-1",
                "Основной",
                "Работа",
                true,
                1000m,
                null,
                null,
                issueAt)
        };
        var stats = new Dictionary<(Guid, string), SubProfileOperationalStats>
        {
            [(AccountId, "sp-1")] = new(0, 0, 1, eventAt)
        };

        var enriched = SubProfileOperationalStatsHelper.Enrich(profiles, AccountId, stats);

        Assert.Equal(eventAt, Assert.Single(enriched!).LastActivityUtc);
    }

    [Fact]
    public void Resolve_SynthesizesProfiles_FromOperationalStats_WhenPersistedProfilesMissing()
    {
        var activityAt = DateTime.UtcNow.AddHours(-1);
        var stats = new Dictionary<(Guid, string), SubProfileOperationalStats>
        {
            [(AccountId, "438814802")] = new(12, 2, 0, activityAt),
            [(AccountId, "438874730")] = new(5, 0, 1, activityAt.AddMinutes(-10))
        };
        var names = new Dictionary<(Guid, string), string>
        {
            [(AccountId, "438814802")] = "Работа вахтой2",
            [(AccountId, "438874730")] = "Кадровый Отдел6"
        };

        var resolved = SubProfileOperationalStatsHelper.Resolve(null, AccountId, stats, names);

        Assert.Equal(2, resolved!.Count);
        Assert.Equal("438814802", resolved[0].Id);
        Assert.Equal("Работа вахтой2", resolved[0].Name);
        Assert.Equal(12, resolved[0].TodayResponses);
        Assert.Equal(2, resolved[0].TodayDuplicates);
        Assert.Equal(activityAt, resolved[0].LastActivityUtc);
        Assert.Equal("438874730", resolved[1].Id);
        Assert.Equal("Кадровый Отдел6", resolved[1].Name);
        Assert.Equal(1, resolved[1].TodayEventErrors);
    }
}
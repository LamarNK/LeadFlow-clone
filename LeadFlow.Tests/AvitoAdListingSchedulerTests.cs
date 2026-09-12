using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAdListingSchedulerTests
{
    private static readonly AvitoAdListingScheduleOptions Options = new()
    {
        ListCheckInterval = TimeSpan.FromHours(12),
        MaxDetailPagesPerRun = 2,
        FreshAgeDays = 20,
        ApproachingDays = 7,
        UnknownDateRecheckAfter = TimeSpan.FromHours(6),
        ApproachingRecheckAfter = TimeSpan.FromHours(12),
        StaleDetailRecheckAfter = TimeSpan.FromHours(72)
    };

    [Fact]
    public void NewCard_OpensDetail()
    {
        var card = new AvitoAdListCard { AvitoItemId = "1", Title = "A", Url = "/x", AgeDays = 1 };
        Assert.True(AvitoAdListingScheduler.ShouldOpenDetail(null, card, DateTime.UtcNow, Options));
    }

    [Fact]
    public void FreshKnownCard_DoesNotOpenDetail()
    {
        var now = DateTime.UtcNow;
        var published = now.AddDays(-3);
        var existing = new AvitoAdListingRecord
        {
            AvitoItemId = "1",
            PublishedAtUtc = published,
            PublicationDateSource = AvitoAdPublicationDateSources.Exact,
            ExpiresAtUtc = AvitoAdExpiryCalculator.ComputeExpiresAtUtc(published),
            DetailCheckedAtUtc = now.AddHours(-1),
            AgeDays = 3,
            StatusText = "",
            IsActive = true
        };
        var card = new AvitoAdListCard { AvitoItemId = "1", AgeDays = 3, StatusText = "" };
        Assert.False(AvitoAdListingScheduler.ShouldOpenDetail(existing, card, now, Options));
    }

    [Fact]
    public void ApproachingExpiry_GetsPriority()
    {
        var now = DateTime.UtcNow;
        var published = now.AddDays(-25);
        var approaching = new AvitoAdListingRecord
        {
            AvitoItemId = "soon",
            PublishedAtUtc = published,
            PublicationDateSource = AvitoAdPublicationDateSources.Exact,
            ExpiresAtUtc = now.AddDays(3),
            DetailCheckedAtUtc = now.AddDays(-2),
            IsActive = true,
            State = AvitoAdListingStates.ApproachingExpiry
        };
        var unknown = new AvitoAdListingRecord
        {
            AvitoItemId = "unknown",
            IsActive = true,
            PublicationDateSource = AvitoAdPublicationDateSources.Unknown,
            State = AvitoAdListingStates.UnknownPublicationDate
        };
        var cards = new Dictionary<string, AvitoAdListCard>
        {
            ["soon"] = new() { AvitoItemId = "soon", AgeDays = 25 },
            ["unknown"] = new() { AvitoItemId = "unknown" }
        };

        var plan = AvitoAdListingScheduler.PlanDetailChecks([approaching, unknown], cards, now, Options);
        Assert.Equal("unknown", plan[0].AvitoItemId);
        Assert.Contains(plan, x => x.AvitoItemId == "soon");
    }

    [Fact]
    public void UnsupportedLayout_MustNotBeTreatedAsSuccessfulEmptyList()
    {
        Assert.False(AvitoProVacancyLayout.IsSupported(AvitoProVacancyLayout.UnsupportedProfileLayout));
        Assert.False(AvitoProVacancyLayout.IsSupported(AvitoProVacancyLayout.NotApplicable));
        Assert.False(AvitoProVacancyLayout.HasProMarkup("<a data-marker=\"view-link\" href=\"/item/1\">x</a>"));
        Assert.True(AvitoProVacancyLayout.HasProMarkup("<div data-marker=\"profile-items-tab\"></div>"));
    }

    [Fact]
    public void IncompleteList_DoesNotDeactivateMissing()
    {
        var now = DateTime.UtcNow;
        var existing = new AvitoAdListingRecord
        {
            Id = Guid.NewGuid(),
            WorkerId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            AvitoItemId = "old",
            IsActive = true,
            CreatedAtUtc = now
        };

        var merged = AvitoAdListingSyncApplier.ApplyListSnapshot(
            [existing],
            existing.WorkerId,
            existing.AccountId,
            "",
            [new AvitoAdListCard { AvitoItemId = "new", Title = "N", Url = "/n" }],
            now,
            listComplete: false);

        Assert.Contains(merged, x => x.AvitoItemId == "old" && x.IsActive);
        Assert.Contains(merged, x => x.AvitoItemId == "new" && x.IsActive);
    }

    [Fact]
    public void CompleteList_DeactivatesMissingAndReactivatesOnReturn()
    {
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var existing = new AvitoAdListingRecord
        {
            Id = Guid.NewGuid(),
            WorkerId = workerId,
            AccountId = accountId,
            AvitoItemId = "gone",
            IsActive = true,
            CreatedAtUtc = now
        };

        var afterComplete = AvitoAdListingSyncApplier.ApplyListSnapshot(
            [existing],
            workerId,
            accountId,
            "",
            [],
            now,
            listComplete: true);
        var deactivated = Assert.Single(afterComplete);
        Assert.False(deactivated.IsActive);
        Assert.Equal(AvitoAdListingStates.NotActive, deactivated.State);

        var afterReturn = AvitoAdListingSyncApplier.ApplyListSnapshot(
            afterComplete,
            workerId,
            accountId,
            "",
            [new AvitoAdListCard { AvitoItemId = "gone", Title = "Back", Url = "/b" }],
            now.AddHours(1),
            listComplete: true);
        var active = Assert.Single(afterReturn);
        Assert.True(active.IsActive);
        Assert.Equal("Back", active.Title);
    }

    [Fact]
    public void ListExpiry_SetsStateWithoutDetailPage()
    {
        var now = new DateTime(2026, 9, 13, 8, 0, 0, DateTimeKind.Utc);
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var expiresAtUtc = new DateTime(2026, 9, 26, 7, 23, 0, DateTimeKind.Utc);

        var result = AvitoAdListingSyncApplier.ApplyListSnapshot(
            [],
            workerId,
            accountId,
            "",
            [
                new AvitoAdListCard
                {
                    AvitoItemId = "8285468940",
                    Title = "Механик",
                    Url = "https://www.avito.ru/volginskiy/vakansii/mehanik_8285468940",
                    AgeDays = 17,
                    ExpiresAtUtc = expiresAtUtc,
                    RemainingDays = 13
                }
            ],
            now,
            listComplete: true);

        var record = Assert.Single(result);
        Assert.Equal(AvitoAdPublicationDateSources.ListExpiry, record.PublicationDateSource);
        Assert.Equal(expiresAtUtc, record.ExpiresAtUtc);
        Assert.Equal(13, record.RemainingDays);
        Assert.Equal(AvitoAdListingStates.Active, record.State);
        Assert.Null(record.DetailCheckedAtUtc);
    }
}

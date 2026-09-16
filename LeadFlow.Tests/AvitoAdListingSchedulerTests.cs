using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Captcha;
using LeadFlow.Core.Services.Worker;
using LeadFlow.Tests.Support;
using WorkerAvitoAdListScheduleDto = Orbita.Contracts.WorkerAvitoAdListScheduleDto;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAdListingSchedulerTests
{
    [Fact]
    public void AdsMonitor_CaptchaContextIdentifiesAccountAndSubProfile()
    {
        var workerId = Guid.NewGuid();
        var account = new AvitoAccount { Id = Guid.NewGuid(), DisplayName = "Avito 111" };
        var subProfile = new AvitoSubProfile { Id = "445352151", Name = "Кадровый отдел Киров 4" };

        var warmup = WorkerAvitoAdsMonitor.CreateCaptchaProviderRequestContext(workerId, account);
        Assert.Equal(workerId, warmup.WorkerId);
        Assert.Equal(account.Id, warmup.AccountId);
        Assert.Equal(CaptchaProviderRequestStages.Other, warmup.Stage);
        Assert.Equal(CaptchaProviderRequestReasons.FirewallDetected, warmup.Reason);

        var switched = WorkerAvitoAdsMonitor.CreateCaptchaProviderRequestContext(workerId, account, subProfile);
        Assert.Equal(subProfile.Id, switched.SubProfileId);
        Assert.Equal(subProfile.Name, switched.SubProfileName);
        Assert.Equal(CaptchaProviderRequestStages.SubProfileSwitch, switched.Stage);
        Assert.Equal(CaptchaProviderRequestReasons.AfterSubProfileSwitch, switched.Reason);
    }

    [Fact]
    public void AdsMonitor_FailedAccountWaitsBeforeAnotherPaidCaptchaAttempt()
    {
        var now = new DateTime(2026, 9, 14, 17, 30, 0, DateTimeKind.Utc);
        var retryAt = now.AddMinutes(MonitoringTiming.AvitoAdsFailureRetryMinutes);

        Assert.True(WorkerAvitoAdsMonitor.IsFailureCooldownActive(retryAt, now));
        Assert.False(WorkerAvitoAdsMonitor.IsFailureCooldownActive(retryAt, retryAt));
    }

    [Fact]
    public void PersistedFutureNextCheck_DoesNotRunAfterWorkerRestart()
    {
        var now = new DateTime(2026, 9, 13, 9, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<WorkerAvitoAdListScheduleDto> schedules =
        [
            new WorkerAvitoAdListScheduleDto(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "sub-1",
                now.AddHours(-1),
                now.AddHours(11),
                now.AddHours(-1))
        ];
        var subProfiles = new List<AvitoSubProfile> { new() { Id = "sub-1", Name = "Подпрофиль" } };

        Assert.False(WorkerAvitoAdsMonitor.IsListDue(schedules, subProfiles, now));
    }

    [Fact]
    public void MissingOrElapsedNextCheck_RunsListMonitor()
    {
        var now = new DateTime(2026, 9, 13, 9, 0, 0, DateTimeKind.Utc);
        var subProfiles = new List<AvitoSubProfile> { new() { Id = "sub-1", Name = "Подпрофиль" } };

        Assert.True(WorkerAvitoAdsMonitor.IsListDue([], subProfiles, now));

        IReadOnlyList<WorkerAvitoAdListScheduleDto> elapsed =
        [
            new WorkerAvitoAdListScheduleDto(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "sub-1",
                now.AddHours(-12),
                now.AddSeconds(-1),
                now.AddHours(-12))
        ];
        Assert.True(WorkerAvitoAdsMonitor.IsListDue(elapsed, subProfiles, now));
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

    [Fact]
    public void ListExpiryFailure_WithKnownExpiry_KeepsExpiryAndClearsStickyError()
    {
        // Карточка истёкшего объявления не содержит даты (Avito её не отдаёт),
        // но срок уже известен из предыдущего парсинга — ошибка не пишется и залипшая очищается.
        var now = new DateTime(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc);
        var knownExpiry = new DateTime(2026, 9, 14, 7, 21, 0, DateTimeKind.Utc);
        var existing = new AvitoAdListingRecord
        {
            Id = Guid.NewGuid(),
            WorkerId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            AvitoItemId = "8166386785",
            IsActive = true,
            CreatedAtUtc = now,
            ExpiresAtUtc = knownExpiry,
            PublicationDateSource = AvitoAdPublicationDateSources.ListExpiry,
            LastParseError = "list_expiry_unparsed"
        };

        var merged = AvitoAdListingSyncApplier.ApplyListSnapshot(
            [existing],
            existing.WorkerId,
            existing.AccountId,
            "",
            [new AvitoAdListCard
            {
                AvitoItemId = "8166386785",
                Title = "Слесарь вахта",
                Url = "/kapustin_yar/vakansii/slesar_8166386785",
                SourceTab = "inactive",
                ExpiryParseError = "list_expiry_unparsed"
            }],
            now,
            listComplete: true);

        var record = Assert.Single(merged);
        Assert.Equal(knownExpiry, record.ExpiresAtUtc);
        Assert.Null(record.LastParseError);
    }

    [Fact]
    public void ListExpiryFailure_WithUnknownExpiry_StillWritesDiagnostic()
    {
        var now = DateTime.UtcNow;

        var merged = AvitoAdListingSyncApplier.ApplyListSnapshot(
            [],
            Guid.NewGuid(),
            Guid.NewGuid(),
            "",
            [new AvitoAdListCard
            {
                AvitoItemId = "1",
                Title = "Тест",
                Url = "/t",
                ExpiryParseError = "list_expiry_unparsed"
            }],
            now,
            listComplete: true);

        var record = Assert.Single(merged);
        Assert.Null(record.ExpiresAtUtc);
        Assert.Equal("list_expiry_unparsed", record.LastParseError);
    }

    [Fact]
    public async Task ListExpiryFailure_WritesStructuredDiagnosticWithCardSample()
    {
        var account = new AvitoAccount { DisplayName = "Avito 103" };
        var card = new AvitoAdListCard
        {
            AvitoItemId = "8312560791",
            ExpiryParseError = "list_expiry_unparsed"
        };
        const string snippet = """
            data-marker="item-snippet/8312560791">
            <span>Активно ещё 18 дней — до 1 окт, 17:33</span>
            """;

        using var capture = GlobalLogCapture.Start();
        await WorkerAvitoAdsMonitor.LogListExpiryParseFailureAsync(
            account,
            "445109158",
            "Кадровый отдел Тюмень 6",
            card,
            snippet);

        var entry = Assert.Single(capture.Entries, x => x.Message.Contains("8312560791", StringComparison.Ordinal));
        Assert.Equal("ads.list_expiry_unparsed", entry.ErrorKey);
        Assert.Equal("list_expiry_parse_failed", entry.Properties["ads.event"]);
        Assert.Equal("8312560791", entry.Properties["ads.avitoItemId"]);
        Assert.Equal("445109158", entry.Properties["ads.subProfileId"]);
        Assert.Equal("list_expiry_unparsed", entry.Properties["ads.reason"]);
        Assert.Equal(snippet, entry.Properties["ads.cardHtml"]);
    }
}

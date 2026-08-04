using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixInstanceSettingsPersistenceTests
{
    [Fact]
    public void FromDto_EmptyOptionalFieldCodesDisablePerInstanceWrites()
    {
        var defaults = new OrbitaBitrixSettings
        {
            DealAgeUfCode = "UF_GLOBAL_AGE",
            DealProfessionUfCode = "UF_GLOBAL_PROFESSION",
            DealCityUfCode = "UF_GLOBAL_CITY"
        };
        var dto = new BitrixInstanceIntegrationSettingsDto(
            "Deal",
            0,
            "Авито",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            true);

        var settings = BitrixInstanceIntegrationSettings.FromDto(dto, defaults);

        Assert.Empty(settings.DealAgeUfCode);
        Assert.Empty(settings.DealProfessionUfCode);
        Assert.Empty(settings.DealCityUfCode);
    }

    [Fact]
    public async Task ResolveWebhookUrlAsync_RejectsUntrustedLegacyHost()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new OrbitaDbContext(options);
        var protector = new WebhookSecretProtector(
            new EphemeralDataProtectionProvider());
        var sut = new BitrixInstanceService(
            db,
            protector,
            null!,
            Options.Create(new OrbitaBitrixSettings()),
            new PanelAuditService(db));
        var instance = new BitrixInstanceEntity
        {
            WebhookUrlProtected = protector.Protect(
                "https://127.0.0.1/rest/1/legacy-secret")
        };

        var resolved = await sut.ResolveWebhookUrlAsync(instance);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task UpdateAsync_PersistsPerPortalSettingsAndNullDoesNotResetThem()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new OrbitaDbContext(options);
        var officeId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.BitrixInstances.Add(new BitrixInstanceEntity
        {
            Id = instanceId,
            OfficeId = officeId,
            Name = "Portal",
            IntegrationSettingsJson = string.Empty,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var defaults = new OrbitaBitrixSettings
        {
            ResponsibleId = 1,
            DealAgeUfCode = "DEFAULT_AGE",
            DealProfessionUfCode = "DEFAULT_PROFESSION",
            DealCityUfCode = "DEFAULT_CITY"
        };
        var sut = new BitrixInstanceService(
            db,
            null!,
            null!,
            Options.Create(defaults),
            new PanelAuditService(db));
        var custom = new BitrixInstanceIntegrationSettingsDto(
            "Deal",
            77,
            "Авито",
            "UF_IDEMPOTENCY",
            "UF_AGE",
            "UF_PROFESSION",
            "UF_CITY",
            false);

        var (updated, firstError) = await sut.UpdateAsync(
            instanceId,
            OfficeScope.ForOffice(officeId),
            officeId: null,
            new UpdateBitrixInstanceRequest("Portal", "", null, custom, true),
            "admin");
        Assert.Null(firstError);
        Assert.Equal(77, updated!.IntegrationSettings.ResponsibleId);
        Assert.False(updated.IntegrationSettings.CheckDuplicatesInBitrix);

        var (renamed, secondError) = await sut.UpdateAsync(
            instanceId,
            OfficeScope.ForOffice(officeId),
            officeId: null,
            new UpdateBitrixInstanceRequest("Renamed", "", null, null, true),
            "admin");

        Assert.Null(secondError);
        Assert.Equal("Renamed", renamed!.Name);
        Assert.Equal(77, renamed.IntegrationSettings.ResponsibleId);
        Assert.Equal("UF_PROFESSION", renamed.IntegrationSettings.DealProfessionUfCode);
        Assert.False(renamed.IntegrationSettings.CheckDuplicatesInBitrix);

        var persisted = BitrixInstanceIntegrationSettings.Parse(
            db.BitrixInstances.Single(x => x.Id == instanceId).IntegrationSettingsJson,
            defaults);
        Assert.False(persisted.CheckDuplicatesInBitrix);
        Assert.False(persisted.ToOrbitaBitrixSettings().CheckDuplicatesInBitrix);
    }

    [Fact]
    public async Task DuplicateCheckDisabled_SkipsBitrixLookupForInstance()
    {
        var instance = new BitrixInstanceEntity
        {
            IntegrationSettingsJson = new BitrixInstanceIntegrationSettings
            {
                CheckDuplicatesInBitrix = false
            }.Serialize(),
            ValidationStatus = BitrixValidationStatuses.NotConfigured
        };
        var sut = new BitrixDuplicateCheckAllService(
            null!,
            null!,
            Options.Create(new OrbitaBitrixSettings
            {
                CheckDuplicatesInBitrix = true
            }));

        var (isDuplicate, unavailableReason) = await sut.CheckInInstanceAsync(
            instance,
            new CandidateMatchProfile("Иван Иванов", 30, "Москва", "79990000000"));

        Assert.False(isDuplicate);
        Assert.Null(unavailableReason);
    }

    [Fact]
    public async Task UpdateAsync_WhenWebhookChanges_DisablesWorkforceAndCancelsOldPortalState()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new OrbitaDbContext(options);
        var now = DateTime.UtcNow;
        var officeId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var protector = new WebhookSecretProtector(
            new EphemeralDataProtectionProvider());
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = now
        });
        db.BitrixInstances.Add(new BitrixInstanceEntity
        {
            Id = instanceId,
            OfficeId = officeId,
            Name = "Portal",
            PortalHost = "old.bitrix24.ru",
            WebhookUrlProtected = protector.Protect(
                "https://old.bitrix24.ru/rest/1/old-secret"),
            IntegrationSettingsJson = string.Empty,
            IsEnabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        db.BitrixWorkforceConfigurations.Add(new BitrixWorkforceConfigurationEntity
        {
            BitrixInstanceId = instanceId,
            OperationMode = BitrixWorkforceDistribution.WriterMode,
            WriterRulesConfirmed = true,
            TimeZoneId = "Europe/Moscow",
            UpdatedAtUtc = now
        });
        var inbox = new BitrixDealEventInboxEntity
        {
            BitrixInstanceId = instanceId,
            EventName = "ONCRMDEALUPDATE",
            DealId = 123,
            EventKey = "old-portal-event",
            ReceivedAtUtc = now,
            State = BitrixWorkforceInboxStates.Pending,
            NextAttemptAtUtc = now
        };
        db.BitrixDealEventInbox.Add(inbox);
        db.BitrixWorkforceCursors.Add(new BitrixWorkforceCursorEntity
        {
            BitrixInstanceId = instanceId,
            Scenario = BitrixWorkforceDistribution.NewScenario,
            LastAssignedBitrixUserId = 10
        });
        db.BitrixWorkforceDealStates.Add(new BitrixWorkforceDealStateEntity
        {
            BitrixInstanceId = instanceId,
            DealId = 123,
            LastObservedStageId = "NEW",
            UpdatedAtUtc = now
        });
        db.BitrixWorkforceMorningStates.Add(new BitrixWorkforceMorningStateEntity
        {
            BitrixInstanceId = instanceId,
            LocalDate = DateOnly.FromDateTime(now),
            Scenario = BitrixWorkforceDistribution.MissedCallScenario
        });
        await db.SaveChangesAsync();
        db.BitrixWorkforceAssignments.Add(new BitrixWorkforceAssignmentEntity
        {
            Id = Guid.NewGuid(),
            InboxId = inbox.Id,
            BitrixInstanceId = instanceId,
            DealId = 123,
            Scenario = BitrixWorkforceDistribution.NewScenario,
            OperationMode = BitrixWorkforceDistribution.WriterMode,
            FromStageId = "NEW",
            ToStageId = "NEW",
            SelectedResponsibleId = 10,
            Decision = BitrixWorkforceDecisions.Assigned,
            Reason = "Selected",
            CreatedAtUtc = now,
            ConfigurationRevisionAtUtc = now
        });
        await db.SaveChangesAsync();

        var validator = new BitrixWebhookValidator(
            new StubHttpClientFactory(new StubHttpMessageHandler(request =>
            {
                var body = request.RequestUri?.AbsolutePath.EndsWith(
                    "/scope.json",
                    StringComparison.OrdinalIgnoreCase) == true
                    ? """{"result":["crm"]}"""
                    : """{"result":[]}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            })));
        var sut = new BitrixInstanceService(
            db,
            protector,
            validator,
            Options.Create(new OrbitaBitrixSettings()),
            new PanelAuditService(db));

        var (updated, error) = await sut.UpdateAsync(
            instanceId,
            OfficeScope.ForOffice(officeId),
            officeId: null,
            new UpdateBitrixInstanceRequest(
                "Portal",
                "",
                "https://other.bitrix24.ru/rest/2/new-secret",
                null,
                true),
            "admin");

        Assert.Null(error);
        Assert.Equal("other.bitrix24.ru", updated!.PortalHost);
        var configuration = await db.BitrixWorkforceConfigurations.SingleAsync();
        Assert.Equal(BitrixWorkforceDistribution.DisabledMode, configuration.OperationMode);
        Assert.False(configuration.WriterRulesConfirmed);
        var cancelledJob = await db.BitrixDealEventInbox.SingleAsync();
        Assert.Equal(BitrixWorkforceInboxStates.Completed, cancelledJob.State);
        Assert.NotNull(cancelledJob.CompletedAtUtc);
        var cancelledAssignment = await db.BitrixWorkforceAssignments.SingleAsync();
        Assert.Equal(BitrixWorkforceDecisions.Ignored, cancelledAssignment.Decision);
        Assert.Empty(db.BitrixWorkforceCursors);
        Assert.Empty(db.BitrixWorkforceDealStates);
        Assert.Empty(db.BitrixWorkforceMorningStates);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}

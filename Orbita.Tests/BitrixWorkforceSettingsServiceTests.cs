using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixWorkforceSettingsServiceTests
{
    [Fact]
    public async Task WriterGate_RequiresIdenticalConfigurationPreviouslySavedInShadow()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new OrbitaDbContext(options);
        var officeId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var protector = new WebhookSecretProtector(
            new EphemeralDataProtectionProvider());
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
            Name = "Bitrix",
            PortalHost = "example.bitrix24.ru",
            WebhookUrlProtected = protector.Protect(
                "https://example.bitrix24.ru/rest/1/secret"),
            IntegrationSettingsJson = string.Empty,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var defaults = Options.Create(new OrbitaBitrixSettings());
        var instances = new BitrixInstanceService(
            db,
            protector,
            null!,
            defaults,
            new PanelAuditService(db));
        var client = new SettingsBitrixClientStub();
        var sut = new BitrixWorkforceSettingsService(
            db,
            Options.Create(new BitrixWorkforceOptions()),
            instances,
            client);
        var scope = OfficeScope.ForOffice(officeId);

        var (_, disabledError) = await sut.SaveAsync(
            instanceId,
            scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.DisabledMode),
            "admin");
        Assert.Null(disabledError);

        var (rejectedWriter, gateError) = await sut.SaveAsync(
            instanceId,
            scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");
        Assert.Null(rejectedWriter);
        Assert.Contains("shadow", gateError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.ValidateCrmAccessCalls);

        var (shadow, shadowError) = await sut.SaveAsync(
            instanceId,
            scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.ShadowMode),
            "admin");
        Assert.Null(shadowError);
        Assert.Equal(BitrixWorkforceDistribution.ShadowMode, shadow!.OperationMode);

        var (writer, writerError) = await sut.SaveAsync(
            instanceId,
            scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");
        Assert.Null(writerError);
        Assert.Equal(BitrixWorkforceDistribution.WriterMode, writer!.OperationMode);
        Assert.True(writer.WriterRulesConfirmed);
        Assert.Equal(1, client.ValidateCrmAccessCalls);
        Assert.Equal(1, client.GetManagerStatusesCalls);
    }

    private static UpdateBitrixWorkforceSettingsRequest CreateRequest(
        string mode,
        bool writerRulesConfirmed = false) =>
        new(
            mode,
            DealCategoryId: 0,
            TimeZoneId: "Europe/Moscow",
            ManagerUserIds: [10, 20],
            StageRules:
            [
                new BitrixWorkforceStageRuleDto(
                    Id: null,
                    Scenario: BitrixWorkforceDistribution.NewScenario,
                    SourceStageId: "NEW",
                    TargetStageId: "NEW",
                    UsesMorningWindow: false,
                    SortOrder: 0,
                    IsEnabled: true)
            ],
            WriterRulesConfirmed: writerRulesConfirmed);

    private sealed class SettingsBitrixClientStub : IBitrixWorkforceClient
    {
        public int ValidateCrmAccessCalls { get; private set; }
        public int GetManagerStatusesCalls { get; private set; }

        public Task ValidateCrmAccessAsync(
            string webhookUrl,
            CancellationToken ct)
        {
            ValidateCrmAccessCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BitrixWorkforceManagerStatus>> GetManagerStatusesAsync(
            string webhookUrl,
            IReadOnlyList<long> managerIds,
            CancellationToken ct)
        {
            GetManagerStatusesCalls++;
            return Task.FromResult<IReadOnlyList<BitrixWorkforceManagerStatus>>(
                managerIds.Select(x => new BitrixWorkforceManagerStatus(
                    x,
                    IsActive: true,
                    TimemanStatus: BitrixWorkforceDistribution.TimemanOpened)).ToList());
        }

        public Task<BitrixWorkforceDeal> GetDealAsync(
            string webhookUrl,
            long dealId,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> GetDealContactIdsAsync(
            string webhookUrl,
            long dealId,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpdateDealAsync(
            string webhookUrl,
            long dealId,
            IReadOnlyDictionary<string, object?> fields,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpdateContactOwnerAsync(
            string webhookUrl,
            long contactId,
            long responsibleId,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BitrixWorkforceDealRevision>> ListDealsAsync(
            string webhookUrl,
            int categoryId,
            IReadOnlyCollection<string> stageIds,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}

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
        var client = new SettingsBitrixClientStub();
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        var sut = context.Sut;
        var instanceId = context.InstanceId;
        var scope = context.Scope;

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
        Assert.Equal(1, client.ValidateDealFieldsCalls);
        Assert.Equal(
            ["UF_IDEMPOTENCY", "UF_AGE", "UF_PROFESSION", "UF_CITY"],
            client.LastRequiredFieldCodes);
        Assert.Equal(1, client.ValidateDealPipelineCalls);
        Assert.Equal(1, client.GetManagerStatusesCalls);
        Assert.Equal([10L, 20L], client.LastManagerIds);
        Assert.Equal(0, client.LastCategoryId);
        Assert.Equal(["NEW", "IN_PROCESS"], client.LastStageIds);
    }

    [Fact]
    public async Task WriterGate_AllowsClosedManagerButRejectsMissingOrInactiveManager()
    {
        var client = new SettingsBitrixClientStub
        {
            ManagerStatusesFactory = managerIds =>
            [
                new BitrixWorkforceManagerStatus(
                    managerIds[0],
                    IsActive: true,
                    TimemanStatus: "CLOSED"),
                new BitrixWorkforceManagerStatus(
                    managerIds[1],
                    IsActive: false,
                    TimemanStatus: null)
            ]
        };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;

        var (_, shadowError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.ShadowMode),
            "admin");
        Assert.Null(shadowError);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains("20", writerError, StringComparison.Ordinal);
        Assert.Contains("неактив", writerError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([10L, 20L], client.LastManagerIds);
        Assert.Equal(
            BitrixWorkforceDistribution.ShadowMode,
            (await db.BitrixWorkforceConfigurations.SingleAsync()).OperationMode);
    }

    [Fact]
    public async Task WriterGate_RejectsPipelineAccessErrorBeforeCheckingManagers()
    {
        var client = new SettingsBitrixClientStub
        {
            PipelineValidationError = new InvalidOperationException("stage MISSING was not found")
        };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;

        var (_, shadowError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.ShadowMode),
            "admin");
        Assert.Null(shadowError);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains("MISSING", writerError, StringComparison.Ordinal);
        Assert.Equal(1, client.ValidateDealPipelineCalls);
        Assert.Equal(0, client.GetManagerStatusesCalls);
    }

    [Fact]
    public async Task WriterGate_RejectsMissingPerInstanceDealFieldBeforePipelineCheck()
    {
        var client = new SettingsBitrixClientStub
        {
            DealFieldValidationError = new InvalidOperationException(
                "deal field UF_CITY was not found")
        };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;

        var (_, shadowError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.ShadowMode),
            "admin");
        Assert.Null(shadowError);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains("UF_CITY", writerError, StringComparison.Ordinal);
        Assert.Equal(1, client.ValidateDealFieldsCalls);
        Assert.Equal(0, client.ValidateDealPipelineCalls);
        Assert.Equal(0, client.GetManagerStatusesCalls);
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
                    TargetStageId: "IN_PROCESS",
                    UsesMorningWindow: false,
                    SortOrder: 0,
                    IsEnabled: true),
                new BitrixWorkforceStageRuleDto(
                    Id: null,
                    Scenario: BitrixWorkforceDistribution.MissedCallScenario,
                    SourceStageId: "DISABLED_SOURCE",
                    TargetStageId: "DISABLED_TARGET",
                    UsesMorningWindow: false,
                    SortOrder: 1,
                    IsEnabled: false)
            ],
            WriterRulesConfirmed: writerRulesConfirmed);

    private static async Task<SettingsTestContext> CreateContextAsync(
        SettingsBitrixClientStub client)
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new OrbitaDbContext(options);
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
            IntegrationSettingsJson = new BitrixInstanceIntegrationSettings
            {
                DealIdempotencyUfCode = "UF_IDEMPOTENCY",
                DealAgeUfCode = "UF_AGE",
                DealProfessionUfCode = "UF_PROFESSION",
                DealCityUfCode = "UF_CITY"
            }.Serialize(),
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var instances = new BitrixInstanceService(
            db,
            protector,
            null!,
            Options.Create(new OrbitaBitrixSettings()),
            new PanelAuditService(db));
        var sut = new BitrixWorkforceSettingsService(
            db,
            Options.Create(new BitrixWorkforceOptions()),
            Options.Create(new OrbitaBitrixSettings()),
            instances,
            client);
        return new SettingsTestContext(
            db,
            sut,
            instanceId,
            OfficeScope.ForOffice(officeId));
    }

    private sealed record SettingsTestContext(
        OrbitaDbContext Db,
        BitrixWorkforceSettingsService Sut,
        Guid InstanceId,
        OfficeScope Scope);

    private sealed class SettingsBitrixClientStub : IBitrixWorkforceClient
    {
        public int ValidateCrmAccessCalls { get; private set; }
        public int ValidateDealFieldsCalls { get; private set; }
        public int ValidateDealPipelineCalls { get; private set; }
        public int GetManagerStatusesCalls { get; private set; }
        public IReadOnlyList<long> LastManagerIds { get; private set; } = [];
        public int? LastCategoryId { get; private set; }
        public IReadOnlyList<string> LastStageIds { get; private set; } = [];
        public IReadOnlyList<string> LastRequiredFieldCodes { get; private set; } = [];
        public Exception? DealFieldValidationError { get; init; }
        public Exception? PipelineValidationError { get; init; }
        public Func<IReadOnlyList<long>, IReadOnlyList<BitrixWorkforceManagerStatus>>?
            ManagerStatusesFactory { get; init; }

        public Task ValidateCrmAccessAsync(
            string webhookUrl,
            CancellationToken ct)
        {
            ValidateCrmAccessCalls++;
            return Task.CompletedTask;
        }

        public Task ValidateDealFieldsAsync(
            string webhookUrl,
            IReadOnlyCollection<string> requiredFieldCodes,
            CancellationToken ct)
        {
            ValidateDealFieldsCalls++;
            LastRequiredFieldCodes = requiredFieldCodes.ToList();
            return DealFieldValidationError is null
                ? Task.CompletedTask
                : Task.FromException(DealFieldValidationError);
        }

        public Task<IReadOnlyList<BitrixWorkforceManagerStatus>> GetManagerStatusesAsync(
            string webhookUrl,
            IReadOnlyList<long> managerIds,
            CancellationToken ct)
        {
            GetManagerStatusesCalls++;
            LastManagerIds = managerIds.ToList();
            if (ManagerStatusesFactory is not null)
            {
                return Task.FromResult(ManagerStatusesFactory(managerIds));
            }

            return Task.FromResult<IReadOnlyList<BitrixWorkforceManagerStatus>>(
                managerIds.Select((x, index) => new BitrixWorkforceManagerStatus(
                    x,
                    IsActive: true,
                    TimemanStatus: index == 0
                        ? BitrixWorkforceDistribution.TimemanOpened
                        : "CLOSED")).ToList());
        }

        public Task ValidateDealPipelineAsync(
            string webhookUrl,
            int categoryId,
            IReadOnlyCollection<string> stageIds,
            CancellationToken ct)
        {
            ValidateDealPipelineCalls++;
            LastCategoryId = categoryId;
            LastStageIds = stageIds.ToList();
            return PipelineValidationError is null
                ? Task.CompletedTask
                : Task.FromException(PipelineValidationError);
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

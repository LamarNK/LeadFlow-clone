using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixWorkforceMultiOfficeTests
{
    [Fact]
    public async Task ShadowMode_IsolatesSameDealIdAcrossTwoOffices()
    {
        var dbOptions = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new OrbitaDbContext(dbOptions);
        var now = new DateTimeOffset(2026, 8, 4, 6, 0, 0, TimeSpan.Zero);
        var time = new FixedTimeProvider(now);
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var defaults = new OrbitaBitrixSettings
        {
            DealIdempotencyUfCode = "UF_IDEMPOTENCY",
            DealAgeUfCode = "UF_AGE",
            DealProfessionUfCode = "UF_PROFESSION",
            DealCityUfCode = "UF_CITY"
        };
        var instances = new BitrixInstanceService(
            db,
            protector,
            null!,
            Options.Create(defaults),
            new PanelAuditService(db));
        var client = new MultiPortalBitrixClient();
        var processor = new BitrixWorkforceProcessor(
            db,
            instances,
            client,
            Options.Create(defaults),
            Options.Create(new BitrixWorkforceOptions
            {
                BatchSize = 50,
                LeaseSeconds = 60
            }),
            time,
            NullLogger<BitrixWorkforceProcessor>.Instance);

        var officeA = Guid.NewGuid();
        var officeB = Guid.NewGuid();
        var instanceA = Guid.NewGuid();
        var instanceB = Guid.NewGuid();
        const string webhookA = "https://office-a.bitrix24.ru/rest/1/secret";
        const string webhookB = "https://office-b.bitrix24.ru/rest/2/secret";
        const long sharedDealId = 1001;

        AddOffice(db, officeA, "Office A", now.UtcDateTime);
        AddOffice(db, officeB, "Office B", now.UtcDateTime);
        AddInstance(db, protector, instanceA, officeA, "Bitrix A", webhookA, now.UtcDateTime);
        AddInstance(db, protector, instanceB, officeB, "Bitrix B", webhookB, now.UtcDateTime);
        AddWorkforce(db, instanceA, managerId: 10, now.UtcDateTime);
        AddWorkforce(db, instanceB, managerId: 20, now.UtcDateTime);
        AddEvent(db, instanceA, sharedDealId, "office-a-event", now.UtcDateTime);
        AddEvent(db, instanceB, sharedDealId, "office-b-event", now.UtcDateTime);

        client.AddPortal(webhookA, sharedDealId, managerId: 10);
        client.AddPortal(webhookB, sharedDealId, managerId: 20);
        await db.SaveChangesAsync();

        var processed = await processor.ProcessBatchAsync();

        Assert.Equal(2, processed);
        var assignments = await db.BitrixWorkforceAssignments
            .AsNoTracking()
            .ToDictionaryAsync(x => x.BitrixInstanceId);
        Assert.Equal(10, assignments[instanceA].SelectedResponsibleId);
        Assert.Equal(20, assignments[instanceB].SelectedResponsibleId);
        Assert.All(assignments.Values, x => Assert.Equal(sharedDealId, x.DealId));
        Assert.All(assignments.Values, x => Assert.Equal(BitrixWorkforceDecisions.Assigned, x.Decision));

        var cursors = await db.BitrixWorkforceCursors
            .AsNoTracking()
            .ToDictionaryAsync(x => x.BitrixInstanceId);
        Assert.Equal(10, cursors[instanceA].LastAssignedBitrixUserId);
        Assert.Equal(20, cursors[instanceB].LastAssignedBitrixUserId);
        Assert.All(
            db.BitrixDealEventInbox,
            x => Assert.Equal(BitrixWorkforceInboxStates.Completed, x.State));
        Assert.Empty(client.DealUpdates);
    }

    private static void AddOffice(
        OrbitaDbContext db,
        Guid officeId,
        string name,
        DateTime now) =>
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = name,
            RegistrationSecretHash = "hash",
            CreatedAtUtc = now
        });

    private static void AddInstance(
        OrbitaDbContext db,
        WebhookSecretProtector protector,
        Guid instanceId,
        Guid officeId,
        string name,
        string webhookUrl,
        DateTime now) =>
        db.BitrixInstances.Add(new BitrixInstanceEntity
        {
            Id = instanceId,
            OfficeId = officeId,
            Name = name,
            PortalHost = new Uri(webhookUrl).Host,
            WebhookUrlProtected = protector.Protect(webhookUrl),
            IntegrationSettingsJson = new BitrixInstanceIntegrationSettings
            {
                DealIdempotencyUfCode = "UF_IDEMPOTENCY",
                DealAgeUfCode = "UF_AGE",
                DealProfessionUfCode = "UF_PROFESSION",
                DealCityUfCode = "UF_CITY"
            }.Serialize(),
            IsEnabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

    private static void AddWorkforce(
        OrbitaDbContext db,
        Guid instanceId,
        long managerId,
        DateTime now)
    {
        db.BitrixWorkforceConfigurations.Add(new BitrixWorkforceConfigurationEntity
        {
            BitrixInstanceId = instanceId,
            OperationMode = BitrixWorkforceDistribution.ShadowMode,
            DealCategoryId = 0,
            TimeZoneId = "Europe/Moscow",
            MorningWindowStartMinutes = 480,
            MorningWindowEndMinutes = 660,
            LateJoinReserveMinutes = 0,
            SingleManagerInitialReleasePercent = 50,
            RetryDelaySeconds = 60,
            MaxAttempts = 10,
            PreserveManualNewOwner = true,
            SyncContactOwner = false,
            FillOnlyEmptyAvitoFields = true,
            WriterRulesConfirmed = false,
            UpdatedAtUtc = now
        });
        db.BitrixWorkforceManagers.Add(new BitrixWorkforceManagerEntity
        {
            Id = Guid.NewGuid(),
            BitrixInstanceId = instanceId,
            BitrixUserId = managerId,
            IsEnabled = true,
            SortOrder = 0
        });
        db.BitrixWorkforceStageRules.Add(new BitrixWorkforceStageRuleEntity
        {
            Id = Guid.NewGuid(),
            BitrixInstanceId = instanceId,
            Scenario = BitrixWorkforceDistribution.NewScenario,
            SourceStageId = "NEW",
            TargetStageId = "NEW",
            UsesMorningWindow = false,
            SortOrder = 0,
            IsEnabled = true
        });
    }

    private static void AddEvent(
        OrbitaDbContext db,
        Guid instanceId,
        long dealId,
        string eventKey,
        DateTime now) =>
        db.BitrixDealEventInbox.Add(new BitrixDealEventInboxEntity
        {
            BitrixInstanceId = instanceId,
            EventName = "ONCRMDEALUPDATE",
            DealId = dealId,
            EventKey = eventKey,
            ReceivedAtUtc = now,
            State = BitrixWorkforceInboxStates.Pending,
            NextAttemptAtUtc = now
        });

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MultiPortalBitrixClient : IBitrixWorkforceClient
    {
        private readonly Dictionary<string, BitrixWorkforceDeal> _deals =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BitrixWorkforceManagerStatus> _statuses =
            new(StringComparer.OrdinalIgnoreCase);

        public List<(string WebhookUrl, long DealId)> DealUpdates { get; } = [];

        public void AddPortal(string webhookUrl, long dealId, long managerId)
        {
            _deals[Key(webhookUrl, dealId)] = new BitrixWorkforceDeal(
                dealId,
                CategoryId: 0,
                StageId: "NEW",
                AssignedById: null,
                CreatedById: 999,
                ModifiedById: 999,
                Comments: string.Empty,
                Fields: new Dictionary<string, string?>());
            _statuses[webhookUrl] = new BitrixWorkforceManagerStatus(
                managerId,
                IsActive: true,
                TimemanStatus: "OPENED");
        }

        public Task<BitrixWorkforceDeal> GetDealAsync(
            string webhookUrl,
            long dealId,
            CancellationToken ct) =>
            Task.FromResult(_deals[Key(webhookUrl, dealId)]);

        public Task<IReadOnlyList<BitrixWorkforceManagerStatus>> GetManagerStatusesAsync(
            string webhookUrl,
            IReadOnlyList<long> managerIds,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BitrixWorkforceManagerStatus>>(
                [_statuses[webhookUrl]]);

        public Task<IReadOnlyList<long>> GetDealContactIdsAsync(
            string webhookUrl,
            long dealId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<long>>([]);

        public Task UpdateDealAsync(
            string webhookUrl,
            long dealId,
            IReadOnlyDictionary<string, object?> fields,
            CancellationToken ct)
        {
            DealUpdates.Add((webhookUrl, dealId));
            return Task.CompletedTask;
        }

        public Task UpdateContactOwnerAsync(
            string webhookUrl,
            long contactId,
            long responsibleId,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<BitrixWorkforceDealRevision>> ListDealsAsync(
            string webhookUrl,
            int categoryId,
            IReadOnlyCollection<string> stageIds,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BitrixWorkforceDealRevision>>([]);

        public Task ValidateCrmAccessAsync(
            string webhookUrl,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task ValidateDealFieldsAsync(
            string webhookUrl,
            IReadOnlyCollection<string> requiredFieldCodes,
            CancellationToken ct) =>
            Task.CompletedTask;

        public Task ValidateDealPipelineAsync(
            string webhookUrl,
            int categoryId,
            IReadOnlyCollection<string> stageIds,
            CancellationToken ct) =>
            Task.CompletedTask;

        private static string Key(string webhookUrl, long dealId) =>
            $"{webhookUrl.TrimEnd('/')}|{dealId}";
    }
}

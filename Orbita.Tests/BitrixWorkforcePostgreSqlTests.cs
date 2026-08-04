using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixWorkforcePostgreSqlTests
{
    private const string ConnectionStringVariable =
        "ORBITA_TEST_POSTGRES_CONNECTION_STRING";
    private const string PreviousWorkforceMigration =
        "20260803231231_AddBitrixWorkforceDistribution";
    private const string ScenarioGuardMigration =
        "20260804120000_AddWorkforceScenarioEntryGuard";

    [PostgreSqlFact]
    public async Task ScenarioGuardMigration_BackfillsLegacyProtectedWorkforceStates()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable)!;
        var schema = $"workforce_migration_{Guid.NewGuid():N}";
        var schemaConnectionString =
            $"{baseConnectionString.Trim().TrimEnd(';')};Search Path={schema}";
        var bootstrapOptions = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseNpgsql(baseConnectionString)
            .Options;

        await using (var bootstrap = new OrbitaDbContext(bootstrapOptions))
        {
            await bootstrap.Database.ExecuteSqlRawAsync(
                $"CREATE SCHEMA \"{schema}\"");
        }

        try
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseNpgsql(schemaConnectionString)
                .Options;
            await using (var migrationDb = new OrbitaDbContext(options))
            {
                await migrationDb.GetService<IMigrator>()
                    .MigrateAsync(PreviousWorkforceMigration);
            }

            var now = new DateTime(2026, 8, 4, 6, 0, 0, DateTimeKind.Utc);
            var officeId = Guid.NewGuid();
            var instanceId = Guid.NewGuid();
            var relevantAssignmentId = Guid.NewGuid();
            var ignoredAssignmentId = Guid.NewGuid();
            var repeatedDeferredAssignmentId = Guid.NewGuid();

            await using (var seed = new OrbitaDbContext(options))
            {
                seed.Offices.Add(new OfficeEntity
                {
                    Id = officeId,
                    Name = "Workforce migration test",
                    RegistrationSecretHash = "hash",
                    CreatedAtUtc = now
                });
                seed.BitrixInstances.Add(new BitrixInstanceEntity
                {
                    Id = instanceId,
                    OfficeId = officeId,
                    Name = "Legacy Bitrix",
                    PortalHost = "legacy.bitrix24.ru",
                    WebhookUrlProtected = "protected",
                    IntegrationSettingsJson = "{}",
                    IsEnabled = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                seed.BitrixWorkforceStageRules.AddRange(
                    new BitrixWorkforceStageRuleEntity
                    {
                        Id = Guid.NewGuid(),
                        BitrixInstanceId = instanceId,
                        Scenario = BitrixWorkforceDistribution.MissedCallScenario,
                        SourceStageId = "A_SOURCE",
                        TargetStageId = "A_TARGET",
                        UsesMorningWindow = false,
                        SortOrder = 0,
                        IsEnabled = true
                    },
                    new BitrixWorkforceStageRuleEntity
                    {
                        Id = Guid.NewGuid(),
                        BitrixInstanceId = instanceId,
                        Scenario = BitrixWorkforceDistribution.SubstituteMissedCallScenario,
                        SourceStageId = "B_SOURCE",
                        TargetStageId = "B_TARGET",
                        UsesMorningWindow = false,
                        SortOrder = 1,
                        IsEnabled = true
                    },
                    new BitrixWorkforceStageRuleEntity
                    {
                        Id = Guid.NewGuid(),
                        BitrixInstanceId = instanceId,
                        Scenario = BitrixWorkforceDistribution.MissedCallScenario,
                        SourceStageId = "A_THIRD",
                        TargetStageId = "A_THIRD_TARGET",
                        UsesMorningWindow = false,
                        SortOrder = 2,
                        IsEnabled = true
                    });
                var relevantInbox = NewEvent(instanceId, 8101, "legacy-relevant", now);
                var newerIrrelevantInbox = NewEvent(instanceId, 8101, "legacy-irrelevant", now);
                var ignoredInbox = NewEvent(instanceId, 8102, "legacy-ignored", now);
                var repeatedDeferredInbox = NewEvent(
                    instanceId,
                    8103,
                    "legacy-repeated-deferred",
                    now);
                repeatedDeferredInbox.AttemptCount = 2;
                var failedBeforeDecisionInbox = NewEvent(
                    instanceId,
                    8104,
                    "legacy-failed-before-decision",
                    now);
                failedBeforeDecisionInbox.AttemptCount = 1;
                failedBeforeDecisionInbox.FailureCount = 1;
                seed.BitrixDealEventInbox.AddRange(
                    relevantInbox,
                    newerIrrelevantInbox,
                    ignoredInbox,
                    repeatedDeferredInbox,
                    failedBeforeDecisionInbox);
                await seed.SaveChangesAsync();

                seed.BitrixWorkforceAssignments.AddRange(
                    new BitrixWorkforceAssignmentEntity
                    {
                        Id = relevantAssignmentId,
                        InboxId = relevantInbox.Id,
                        BitrixInstanceId = instanceId,
                        DealId = 8101,
                        Scenario = BitrixWorkforceDistribution.MissedCallScenario,
                        OperationMode = BitrixWorkforceDistribution.WriterMode,
                        FromStageId = "A_SOURCE",
                        ToStageId = "A_TARGET",
                        PreviousResponsibleId = 999,
                        SelectedResponsibleId = 10,
                        Decision = BitrixWorkforceDecisions.Assigned,
                        Reason = "Legacy partial writer saga",
                        CreatedAtUtc = now.AddMinutes(-5),
                        DealAppliedAtUtc = now.AddMinutes(-4)
                    },
                    new BitrixWorkforceAssignmentEntity
                    {
                        Id = Guid.NewGuid(),
                        InboxId = newerIrrelevantInbox.Id,
                        BitrixInstanceId = instanceId,
                        DealId = 8101,
                        Scenario = BitrixWorkforceDistribution.SubstituteMissedCallScenario,
                        OperationMode = BitrixWorkforceDistribution.WriterMode,
                        FromStageId = "B_SOURCE",
                        ToStageId = "B_TARGET",
                        PreviousResponsibleId = 10,
                        SelectedResponsibleId = 20,
                        Decision = BitrixWorkforceDecisions.Assigned,
                        Reason = "Newer assignment from another scenario",
                        CreatedAtUtc = now.AddMinutes(-1)
                    },
                    new BitrixWorkforceAssignmentEntity
                    {
                        Id = ignoredAssignmentId,
                        InboxId = ignoredInbox.Id,
                        BitrixInstanceId = instanceId,
                        DealId = 8102,
                        Scenario = BitrixWorkforceDistribution.MissedCallScenario,
                        OperationMode = BitrixWorkforceDistribution.WriterMode,
                        FromStageId = "A_SOURCE",
                        ToStageId = "A_TARGET",
                        PreviousResponsibleId = 999,
                        SelectedResponsibleId = 30,
                        Decision = BitrixWorkforceDecisions.Ignored,
                        Reason = "Legacy selected assignment cancelled before write",
                        CreatedAtUtc = now.AddMinutes(-3)
                    },
                    new BitrixWorkforceAssignmentEntity
                    {
                        Id = repeatedDeferredAssignmentId,
                        InboxId = repeatedDeferredInbox.Id,
                        BitrixInstanceId = instanceId,
                        DealId = 8103,
                        Scenario = BitrixWorkforceDistribution.MissedCallScenario,
                        OperationMode = BitrixWorkforceDistribution.WriterMode,
                        FromStageId = "A_SOURCE",
                        ToStageId = "A_TARGET",
                        PreviousResponsibleId = 36,
                        SelectedResponsibleId = null,
                        Decision = BitrixWorkforceDecisions.Deferred,
                        Reason = "Legacy deferred assignment retried more than once",
                        CreatedAtUtc = now.AddMinutes(-2)
                    });
                await seed.SaveChangesAsync();

                await seed.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO "BitrixWorkforceDealStates" (
                         "BitrixInstanceId",
                         "DealId",
                         "LastObservedStageId",
                         "LastAppliedStageId",
                         "LastAppliedResponsibleId",
                         "LastAssignmentId",
                         "UpdatedAtUtc")
                     VALUES
                     (
                         {instanceId},
                         {8101L},
                         {"A_TARGET"},
                         NULL,
                         NULL,
                         NULL,
                         {now.AddMinutes(-10)}),
                     (
                         {instanceId},
                         {8102L},
                         {"A_THIRD"},
                         NULL,
                         NULL,
                         NULL,
                         {now.AddMinutes(-10)}),
                     (
                         {instanceId},
                         {8103L},
                         {"A_TARGET"},
                         NULL,
                         NULL,
                         NULL,
                         {now.AddMinutes(-10)}),
                     (
                         {instanceId},
                         {8104L},
                         {"A_TARGET"},
                         NULL,
                         NULL,
                         NULL,
                         {now.AddMinutes(-10)})
                     """);
            }

            await using (var migrationDb = new OrbitaDbContext(options))
            {
                await migrationDb.GetService<IMigrator>()
                    .MigrateAsync(ScenarioGuardMigration);
            }

            await using var verify = new OrbitaDbContext(options);
            var state = await verify.BitrixWorkforceDealStates
                .AsNoTracking()
                .SingleAsync(x => x.BitrixInstanceId == instanceId && x.DealId == 8101);
            Assert.Equal(BitrixWorkforceDistribution.MissedCallScenario, state.ActiveScenario);
            Assert.Equal(now.AddMinutes(-4), state.ActiveScenarioWriterHandledAtUtc);
            Assert.Null(state.ActiveScenarioShadowHandledAtUtc);
            Assert.Equal("A_TARGET", state.LastAppliedStageId);
            Assert.Equal(10, state.LastAppliedResponsibleId);
            Assert.Equal(relevantAssignmentId, state.LastAssignmentId);

            var ignoredState = await verify.BitrixWorkforceDealStates
                .AsNoTracking()
                .SingleAsync(x => x.BitrixInstanceId == instanceId && x.DealId == 8102);
            Assert.Equal(
                BitrixWorkforceDistribution.MissedCallScenario,
                ignoredState.ActiveScenario);
            Assert.Equal(now.AddMinutes(-3), ignoredState.ActiveScenarioWriterHandledAtUtc);
            Assert.Null(ignoredState.LastAppliedStageId);
            Assert.Null(ignoredState.LastAppliedResponsibleId);
            Assert.Equal(ignoredAssignmentId, ignoredState.LastAssignmentId);

            var deferredState = await verify.BitrixWorkforceDealStates
                .AsNoTracking()
                .SingleAsync(x => x.BitrixInstanceId == instanceId && x.DealId == 8103);
            Assert.Equal(
                BitrixWorkforceDistribution.MissedCallScenario,
                deferredState.ActiveScenario);
            Assert.Equal(now.AddMinutes(-2), deferredState.ActiveScenarioWriterHandledAtUtc);
            Assert.Null(deferredState.LastAppliedStageId);
            Assert.Null(deferredState.LastAppliedResponsibleId);
            Assert.Equal(repeatedDeferredAssignmentId, deferredState.LastAssignmentId);

            var preDecisionFailureState = await verify.BitrixWorkforceDealStates
                .AsNoTracking()
                .SingleAsync(x => x.BitrixInstanceId == instanceId && x.DealId == 8104);
            Assert.Equal(
                BitrixWorkforceDistribution.MissedCallScenario,
                preDecisionFailureState.ActiveScenario);
            Assert.Equal(now, preDecisionFailureState.ActiveScenarioShadowHandledAtUtc);
            Assert.Equal(now, preDecisionFailureState.ActiveScenarioWriterHandledAtUtc);
            Assert.Null(preDecisionFailureState.LastAssignmentId);
        }
        finally
        {
            await using var cleanup = new OrbitaDbContext(bootstrapOptions);
            await cleanup.Database.ExecuteSqlRawAsync(
                $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    [PostgreSqlFact]
    public async Task ConcurrentDuplicateEvents_ApplyOneAssignmentAndAdvanceCursorOnce()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable)!;
        var schema = $"workforce_{Guid.NewGuid():N}";
        var schemaConnectionString =
            $"{baseConnectionString.Trim().TrimEnd(';')};Search Path={schema}";

        var bootstrapOptions = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseNpgsql(baseConnectionString)
            .Options;
        await using (var bootstrap = new OrbitaDbContext(bootstrapOptions))
        {
            await bootstrap.Database.ExecuteSqlRawAsync(
                $"CREATE SCHEMA \"{schema}\"");
        }

        try
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseNpgsql(schemaConnectionString)
                .Options;
            await using (var migrationDb = new OrbitaDbContext(options))
            {
                await migrationDb.Database.MigrateAsync();
            }

            var now = new DateTimeOffset(2026, 8, 4, 6, 0, 0, TimeSpan.Zero);
            var time = new FixedTimeProvider(now);
            var provider = new EphemeralDataProtectionProvider();
            var protector = new WebhookSecretProtector(provider);
            var defaults = new OrbitaBitrixSettings
            {
                DealIdempotencyUfCode = "UF_IDEMPOTENCY",
                DealAgeUfCode = "UF_AGE",
                DealProfessionUfCode = "UF_PROFESSION",
                DealCityUfCode = "UF_CITY"
            };
            var officeId = Guid.NewGuid();
            var instanceId = Guid.NewGuid();
            const string webhookUrl =
                "https://postgres-test.bitrix24.ru/rest/1/secret";
            const long dealId = 7001;

            await using (var seed = new OrbitaDbContext(options))
            {
                seed.Offices.Add(new OfficeEntity
                {
                    Id = officeId,
                    Name = "PostgreSQL workforce test",
                    RegistrationSecretHash = "hash",
                    CreatedAtUtc = now.UtcDateTime
                });
                seed.BitrixInstances.Add(new BitrixInstanceEntity
                {
                    Id = instanceId,
                    OfficeId = officeId,
                    Name = "PostgreSQL Bitrix",
                    PortalHost = "postgres-test.bitrix24.ru",
                    WebhookUrlProtected = protector.Protect(webhookUrl),
                    IntegrationSettingsJson = new BitrixInstanceIntegrationSettings
                    {
                        DealIdempotencyUfCode = "UF_IDEMPOTENCY",
                        DealAgeUfCode = "UF_AGE",
                        DealProfessionUfCode = "UF_PROFESSION",
                        DealCityUfCode = "UF_CITY"
                    }.Serialize(),
                    IsEnabled = true,
                    CreatedAtUtc = now.UtcDateTime,
                    UpdatedAtUtc = now.UtcDateTime
                });
                seed.BitrixWorkforceConfigurations.Add(
                    new BitrixWorkforceConfigurationEntity
                    {
                        BitrixInstanceId = instanceId,
                        OperationMode = BitrixWorkforceDistribution.WriterMode,
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
                        WriterRulesConfirmed = true,
                        UpdatedAtUtc = now.UtcDateTime
                    });
                seed.BitrixWorkforceManagers.Add(new BitrixWorkforceManagerEntity
                {
                    Id = Guid.NewGuid(),
                    BitrixInstanceId = instanceId,
                    BitrixUserId = 10,
                    IsEnabled = true,
                    SortOrder = 0
                });
                seed.BitrixWorkforceStageRules.Add(
                    new BitrixWorkforceStageRuleEntity
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
                seed.BitrixDealEventInbox.AddRange(
                    NewEvent(instanceId, dealId, "duplicate-a", now.UtcDateTime),
                    NewEvent(instanceId, dealId, "duplicate-b", now.UtcDateTime));
                await seed.SaveChangesAsync();
            }

            var client = new ConcurrentBitrixClient(dealId);
            await using var dbA = new OrbitaDbContext(options);
            await using var dbB = new OrbitaDbContext(options);
            var processorA = CreateProcessor(
                dbA,
                protector,
                defaults,
                client,
                time);
            var processorB = CreateProcessor(
                dbB,
                protector,
                defaults,
                client,
                time);

            await Task.WhenAll(
                processorA.ProcessBatchAsync(),
                processorB.ProcessBatchAsync());

            await using var verify = new OrbitaDbContext(options);
            var assignments = await verify.BitrixWorkforceAssignments
                .AsNoTracking()
                .Where(x => x.BitrixInstanceId == instanceId && x.DealId == dealId)
                .ToListAsync();
            Assert.Single(assignments.Where(x =>
                x.Decision == BitrixWorkforceDecisions.Assigned));
            Assert.Equal(1, client.DealUpdateCount);
            Assert.Equal(
                10,
                await verify.BitrixWorkforceCursors
                    .Where(x =>
                        x.BitrixInstanceId == instanceId
                        && x.Scenario == BitrixWorkforceDistribution.NewScenario)
                    .Select(x => x.LastAssignedBitrixUserId)
                    .SingleAsync());
            Assert.All(
                await verify.BitrixDealEventInbox.AsNoTracking().ToListAsync(),
                x => Assert.Equal(BitrixWorkforceInboxStates.Completed, x.State));
        }
        finally
        {
            await using var cleanup = new OrbitaDbContext(bootstrapOptions);
            await cleanup.Database.ExecuteSqlRawAsync(
                $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    private static BitrixDealEventInboxEntity NewEvent(
        Guid instanceId,
        long dealId,
        string eventKey,
        DateTime now) =>
        new()
        {
            BitrixInstanceId = instanceId,
            EventName = "ONCRMDEALUPDATE",
            DealId = dealId,
            EventKey = eventKey,
            ReceivedAtUtc = now,
            State = BitrixWorkforceInboxStates.Pending,
            NextAttemptAtUtc = now
        };

    private static BitrixWorkforceProcessor CreateProcessor(
        OrbitaDbContext db,
        WebhookSecretProtector protector,
        OrbitaBitrixSettings defaults,
        IBitrixWorkforceClient client,
        TimeProvider time)
    {
        var instances = new BitrixInstanceService(
            db,
            protector,
            null!,
            Options.Create(defaults),
            new PanelAuditService(db));
        return new BitrixWorkforceProcessor(
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
    }

    private sealed class PostgreSqlFactAttribute : FactAttribute
    {
        public PostgreSqlFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(ConnectionStringVariable)))
            {
                Skip =
                    $"Set {ConnectionStringVariable} to run PostgreSQL concurrency tests.";
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ConcurrentBitrixClient(long dealId) : IBitrixWorkforceClient
    {
        private readonly object _sync = new();
        private BitrixWorkforceDeal _deal = new(
            dealId,
            CategoryId: 0,
            StageId: "NEW",
            AssignedById: null,
            CreatedById: 999,
            ModifiedById: 999,
            Comments: string.Empty,
            Fields: new ConcurrentDictionary<string, string?>());
        private int _dealUpdateCount;

        public int DealUpdateCount => Volatile.Read(ref _dealUpdateCount);

        public async Task<BitrixWorkforceDeal> GetDealAsync(
            string webhookUrl,
            long requestedDealId,
            CancellationToken ct)
        {
            Assert.Equal(dealId, requestedDealId);
            await Task.Delay(75, ct);
            lock (_sync)
            {
                return _deal;
            }
        }

        public Task<IReadOnlyList<BitrixWorkforceManagerStatus>> GetManagerStatusesAsync(
            string webhookUrl,
            IReadOnlyList<long> managerIds,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BitrixWorkforceManagerStatus>>(
                [new(10, true, "OPENED")]);

        public Task<IReadOnlyList<long>> GetDealContactIdsAsync(
            string webhookUrl,
            long requestedDealId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<long>>([]);

        public Task<long?> GetContactOwnerIdAsync(
            string webhookUrl,
            long contactId,
            CancellationToken ct) =>
            Task.FromResult<long?>(null);

        public Task UpdateDealAsync(
            string webhookUrl,
            long requestedDealId,
            IReadOnlyDictionary<string, object?> fields,
            CancellationToken ct)
        {
            Assert.Equal(dealId, requestedDealId);
            lock (_sync)
            {
                var responsible = fields.TryGetValue(
                                      "ASSIGNED_BY_ID",
                                      out var responsibleValue)
                                  && long.TryParse(
                                      responsibleValue?.ToString(),
                                      out var parsedResponsible)
                    ? parsedResponsible
                    : _deal.AssignedById;
                var stage = fields.TryGetValue("STAGE_ID", out var stageValue)
                    ? stageValue?.ToString() ?? _deal.StageId
                    : _deal.StageId;
                _deal = _deal with
                {
                    StageId = stage,
                    AssignedById = responsible
                };
                Interlocked.Increment(ref _dealUpdateCount);
            }

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
    }
}

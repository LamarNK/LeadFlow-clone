using System.Globalization;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class BitrixWorkforceProcessor(
    OrbitaDbContext db,
    BitrixInstanceService bitrixInstances,
    IBitrixWorkforceClient bitrixClient,
    IOptions<OrbitaBitrixSettings> defaultBitrixOptions,
    IOptions<BitrixWorkforceOptions> workforceOptions,
    TimeProvider timeProvider,
    ILogger<BitrixWorkforceProcessor> logger)
{
    private readonly Dictionary<Guid, ManagerStatusCacheEntry> _managerStatusCache = [];

    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        var workerId = $"{Environment.MachineName}:{Environment.ProcessId}";
        var batchSize = Math.Clamp(workforceOptions.Value.BatchSize, 1, 200);
        var processed = 0;
        while (processed < batchSize)
        {
            var now = UtcNow();
            var leaseUntil = now.AddSeconds(
                Math.Clamp(workforceOptions.Value.LeaseSeconds, 60, 1800));
            var jobs = await LeaseJobsAsync(
                now,
                leaseUntil,
                workerId,
                batchSize: 1,
                ct);
            if (jobs.Count == 0)
            {
                break;
            }

            var job = jobs[0];
            try
            {
                await ProcessAsync(job, workerId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Bitrix workforce job {JobId} for deal {DealId} failed.",
                    job.Id,
                    job.DealId);
                var failedJobId = job.Id;
                db.ChangeTracker.Clear();
                var failedJob = await db.BitrixDealEventInbox
                    .FirstOrDefaultAsync(x => x.Id == failedJobId, ct);
                if (failedJob is not null
                    && failedJob.State == BitrixWorkforceInboxStates.Processing
                    && string.Equals(failedJob.LockOwner, workerId, StringComparison.Ordinal))
                {
                    await HandleFailureAsync(failedJob, ex, ct);
                }
            }

            processed++;
        }

        return processed;
    }

    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        var configurations = await db.BitrixWorkforceConfigurations
            .AsNoTracking()
            .Where(x => x.OperationMode != BitrixWorkforceDistribution.DisabledMode)
            .ToListAsync(ct);
        var added = 0;

        foreach (var configuration in configurations)
        {
            var instance = await db.BitrixInstances
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == configuration.BitrixInstanceId && x.IsEnabled,
                    ct);
            if (instance is null)
            {
                continue;
            }

            var webhookUrl = await bitrixInstances.ResolveWebhookUrlAsync(instance, ct);
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                continue;
            }

            var rules = await db.BitrixWorkforceStageRules
                .AsNoTracking()
                .Where(x => x.BitrixInstanceId == instance.Id && x.IsEnabled)
                .OrderBy(x => x.SortOrder)
                .ToListAsync(ct);
            if (rules.Count == 0)
            {
                continue;
            }

            var activeDealIds = await db.BitrixDealEventInbox
                .AsNoTracking()
                .Where(x => x.BitrixInstanceId == instance.Id
                            && (x.State == BitrixWorkforceInboxStates.Pending
                                || x.State == BitrixWorkforceInboxStates.Processing))
                .Select(x => x.DealId)
                .Distinct()
                .ToListAsync(ct);
            var active = activeDealIds.ToHashSet();
            var now = UtcNow();
            if (!BitrixWorkforceTimeZones.TryResolve(configuration.TimeZoneId, out var timeZone))
            {
                continue;
            }

            var localDate = BitrixWorkforceDistribution.LocalDate(
                new DateTimeOffset(now, TimeSpan.Zero),
                timeZone);
            foreach (var stageId in rules
                         .Select(x => x.SourceStageId)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var deals = await bitrixClient.ListDealsAsync(
                    webhookUrl,
                    configuration.DealCategoryId,
                    [stageId],
                    ct);
                foreach (var deal in deals)
                {
                    var dealId = deal.DealId;
                    if (active.Contains(dealId))
                    {
                        continue;
                    }

                    var eventKey = BuildReconciliationEventKey(
                        instance.Id,
                        dealId,
                        deal.StageId,
                        deal.Revision,
                        localDate);
                    if (await db.BitrixDealEventInbox.AnyAsync(
                            x => x.BitrixInstanceId == instance.Id && x.EventKey == eventKey,
                            ct))
                    {
                        continue;
                    }

                    db.BitrixDealEventInbox.Add(new BitrixDealEventInboxEntity
                    {
                        BitrixInstanceId = instance.Id,
                        EventName = "RECONCILE",
                        DealId = dealId,
                        EventKey = eventKey,
                        ReceivedAtUtc = now,
                        State = BitrixWorkforceInboxStates.Pending,
                        NextAttemptAtUtc = now
                    });
                    active.Add(dealId);
                    added++;
                }
            }

            await db.SaveChangesAsync(ct);
        }

        return added;
    }

    private async Task ProcessAsync(
        BitrixDealEventInboxEntity job,
        string workerId,
        CancellationToken ct)
    {
        await using var dealLock = await AcquireDealLockAsync(
            job.BitrixInstanceId,
            job.DealId,
            ct);
        await db.Entry(job).ReloadAsync(ct);
        if (job.State != BitrixWorkforceInboxStates.Processing
            || !string.Equals(job.LockOwner, workerId, StringComparison.Ordinal))
        {
            return;
        }

        job.LockedUntilUtc = UtcNow().AddSeconds(
            Math.Clamp(workforceOptions.Value.LeaseSeconds, 60, 1800));
        await db.SaveChangesAsync(ct);
        await ProcessLockedAsync(job, ct);
    }

    private async Task ProcessLockedAsync(
        BitrixDealEventInboxEntity job,
        CancellationToken ct)
    {
        var configuration = await db.BitrixWorkforceConfigurations
            .FirstOrDefaultAsync(x => x.BitrixInstanceId == job.BitrixInstanceId, ct);
        var instance = await db.BitrixInstances
            .FirstOrDefaultAsync(x => x.Id == job.BitrixInstanceId, ct);
        if (configuration is null
            || instance is null
            || !instance.IsEnabled
            || configuration.OperationMode == BitrixWorkforceDistribution.DisabledMode)
        {
            await CompleteAsync(job, "disabled", "Workforce distribution is disabled.", ct);
            return;
        }

        var webhookUrl = await bitrixInstances.ResolveWebhookUrlAsync(instance, ct);
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            throw new InvalidOperationException("Bitrix24 webhook URL cannot be decrypted.");
        }

        var rules = await db.BitrixWorkforceStageRules
            .Where(x => x.BitrixInstanceId == instance.Id && x.IsEnabled)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(ct);
        var managers = await db.BitrixWorkforceManagers
            .Where(x => x.BitrixInstanceId == instance.Id && x.IsEnabled)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(ct);
        var managerIds = managers.Select(x => x.BitrixUserId).ToList();
        var integrationSettings = BitrixInstanceIntegrationSettings.Parse(
            instance.IntegrationSettingsJson,
            defaultBitrixOptions.Value);
        var deal = await bitrixClient.GetDealAsync(webhookUrl, job.DealId, ct);
        var dealState = await GetOrCreateDealStateAsync(instance.Id, deal.Id, ct);
        var previouslyObservedStageId = dealState.LastObservedStageId;
        dealState.LastObservedStageId = deal.StageId;
        dealState.UpdatedAtUtc = UtcNow();

        if (deal.CategoryId != configuration.DealCategoryId)
        {
            await CompleteAsync(job, "category_mismatch", "Deal belongs to another funnel.", ct);
            return;
        }

        var assignment = await db.BitrixWorkforceAssignments
            .FirstOrDefaultAsync(x => x.InboxId == job.Id, ct);
        if (assignment?.Decision == BitrixWorkforceDecisions.Assigned
            && assignment.SelectedResponsibleId is > 0)
        {
            if (configuration.UpdatedAtUtc != assignment.ConfigurationRevisionAtUtc)
            {
                await CompleteAsync(
                    job,
                    "configuration_changed",
                    "Distribution settings changed after this assignment was selected.",
                    ct);
                return;
            }

            var remoteMatchesAppliedAssignment =
                string.Equals(
                    deal.StageId,
                    assignment.ToStageId,
                    StringComparison.OrdinalIgnoreCase)
                && deal.AssignedById == assignment.SelectedResponsibleId;
            var dealStillMatchesAssignment = assignment.DealAppliedAtUtc is not null
                ? remoteMatchesAppliedAssignment
                : string.Equals(
                      deal.StageId,
                      assignment.FromStageId,
                      StringComparison.OrdinalIgnoreCase)
                  || remoteMatchesAppliedAssignment;
            if (dealStillMatchesAssignment)
            {
                await ApplyAssignmentAsync(
                    job,
                    assignment,
                    deal,
                    dealState,
                    integrationSettings,
                    configuration,
                    webhookUrl,
                    ct);
                return;
            }

            if (assignment.DealAppliedAtUtc is not null)
            {
                await CompleteAsync(
                    job,
                    "stale_partial_assignment",
                    "Deal or responsible changed after the deal step; contact synchronization was cancelled.",
                    ct);
                return;
            }

            assignment.Decision = BitrixWorkforceDecisions.Ignored;
            assignment.Reason =
                "A previously selected assignment became stale because the deal stage changed.";
            assignment.SelectedResponsibleId = null;
            assignment.Error = null;
        }

        var rule = rules.FirstOrDefault(x =>
            string.Equals(x.SourceStageId, deal.StageId, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            await CompleteAsync(job, "stage_not_configured", "Deal stage is not configured.", ct);
            return;
        }

        if ((assignment is null
             || assignment.Decision != BitrixWorkforceDecisions.Assigned)
            && dealState.LastAssignmentId is not null
            && string.Equals(
                dealState.LastAppliedStageId,
                deal.StageId,
                StringComparison.OrdinalIgnoreCase)
            && (configuration.OperationMode == BitrixWorkforceDistribution.ShadowMode
                ? string.Equals(
                    previouslyObservedStageId,
                    deal.StageId,
                    StringComparison.OrdinalIgnoreCase)
                : dealState.LastAppliedResponsibleId == deal.AssignedById))
        {
            await CompleteAsync(job, "self_update", "State already matches the last applied decision.", ct);
            return;
        }

        if (rule.Scenario == BitrixWorkforceDistribution.NewScenario
            && configuration.PreserveManualNewOwner
            && IsManagerCreatedDeal(deal, managerIds, integrationSettings.DealIdempotencyUfCode))
        {
            await CompleteWithDecisionAsync(
                job,
                assignment,
                rule,
                deal,
                configuration,
                BitrixWorkforceDecisions.Ignored,
                "NEW was created manually by a configured manager.",
                selectedManagerId: null,
                ct);
            return;
        }

        if (!BitrixWorkforceTimeZones.TryResolve(configuration.TimeZoneId, out var timeZone))
        {
            throw new InvalidOperationException($"Unknown time zone: {configuration.TimeZoneId}.");
        }

        var nowOffset = new DateTimeOffset(UtcNow(), TimeSpan.Zero);
        var windowStart = TimeOnly.FromTimeSpan(
            TimeSpan.FromMinutes(configuration.MorningWindowStartMinutes));
        var windowEnd = configuration.MorningWindowEndMinutes == 1440
            ? new TimeOnly(23, 59, 59, 999)
            : TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(configuration.MorningWindowEndMinutes));
        if (rule.UsesMorningWindow
            && !BitrixWorkforceDistribution.IsInsideWindow(
                nowOffset,
                timeZone,
                windowStart,
                windowEnd))
        {
            var next = BitrixWorkforceDistribution.NextWindowStart(
                nowOffset,
                timeZone,
                windowStart,
                windowEnd);
            await DeferWithDecisionAsync(
                job,
                assignment,
                rule,
                deal,
                configuration,
                next.UtcDateTime,
                "Waiting for the morning distribution window.",
                ct);
            return;
        }

        var statuses = await GetManagerStatusesAsync(
            instance.Id,
            webhookUrl,
            managerIds,
            ct);
        var eligible = BitrixWorkforceDistribution.EligibleManagerIds(
            statuses.Select(x => new BitrixWorkforceDistribution.ManagerCandidate(
                x.BitrixUserId,
                x.TimemanStatus,
                x.IsActive)));
        if (eligible.Count == 0)
        {
            await DeferWithDecisionAsync(
                job,
                assignment,
                rule,
                deal,
                configuration,
                UtcNow().AddSeconds(configuration.RetryDelaySeconds),
                "No configured manager has an OPENED or PAUSED workday.",
                ct);
            return;
        }

        var scenarioTransaction = await BeginScenarioTransactionAsync(
            instance.Id,
            rule.Scenario,
            ct);
        try
        {
            var anotherAssignmentInFlight = await db.BitrixWorkforceAssignments
                .AsNoTracking()
                .AnyAsync(
                    x => x.BitrixInstanceId == instance.Id
                         && x.DealId == deal.Id
                         && x.InboxId != job.Id
                         && x.Decision == BitrixWorkforceDecisions.Assigned
                         && x.AppliedAtUtc == null,
                    ct);
            if (anotherAssignmentInFlight)
            {
                await DeferWithDecisionAsync(
                    job,
                    assignment,
                    rule,
                    deal,
                    configuration,
                    UtcNow().AddSeconds(configuration.RetryDelaySeconds),
                    "Another event is already assigning this deal.",
                    ct);
                if (scenarioTransaction is not null)
                {
                    await scenarioTransaction.CommitAsync(ct);
                }

                return;
            }

            BitrixWorkforceMorningStateEntity? morningState = null;
            if (rule.UsesMorningWindow && configuration.LateJoinReserveMinutes > 0)
            {
                morningState = await GetOrCreateMorningStateAsync(
                    instance,
                    configuration,
                    rule,
                    eligible,
                    webhookUrl,
                    timeZone,
                    windowEnd,
                    ct);
                if (eligible.Count == 1
                    && eligible[0] == morningState.FirstManagerId
                    && morningState.ReserveUntilUtc is DateTime reserveUntil
                    && UtcNow() < reserveUntil
                    && morningState.InitialReleasedCount >= morningState.InitialReleaseLimit)
                {
                    await DeferWithDecisionAsync(
                        job,
                        assignment,
                        rule,
                        deal,
                        configuration,
                        Min(
                            reserveUntil,
                            UtcNow().AddSeconds(configuration.RetryDelaySeconds)),
                        "Reserved for a manager who may start the workday later.",
                        ct,
                        BitrixWorkforceDecisions.Reserved);
                    if (scenarioTransaction is not null)
                    {
                        await scenarioTransaction.CommitAsync(ct);
                    }

                    return;
                }
            }

            var cursor = await db.BitrixWorkforceCursors
                .FirstOrDefaultAsync(
                    x => x.BitrixInstanceId == instance.Id && x.Scenario == rule.Scenario,
                    ct);
            if (cursor is null)
            {
                cursor = new BitrixWorkforceCursorEntity
                {
                    BitrixInstanceId = instance.Id,
                    Scenario = rule.Scenario
                };
                db.BitrixWorkforceCursors.Add(cursor);
            }

            var selected = BitrixWorkforceDistribution.SelectNextManager(
                eligible,
                cursor.LastAssignedBitrixUserId);
            if (selected is null)
            {
                await DeferWithDecisionAsync(
                    job,
                    assignment,
                    rule,
                    deal,
                    configuration,
                    UtcNow().AddSeconds(configuration.RetryDelaySeconds),
                    "No manager was selected.",
                    ct);
                if (scenarioTransaction is not null)
                {
                    await scenarioTransaction.CommitAsync(ct);
                }

                return;
            }

            assignment = UpsertAssignment(
                job,
                assignment,
                rule,
                deal,
                configuration,
                BitrixWorkforceDecisions.Assigned,
                "Round-robin among managers with an OPENED or PAUSED workday.",
                selected);
            cursor.LastAssignedBitrixUserId = selected;
            cursor.LastAssignedAtUtc = UtcNow();
            if (morningState is not null
                && eligible.Count == 1
                && eligible[0] == morningState.FirstManagerId
                && morningState.ReserveUntilUtc is DateTime morningReserveUntil
                && UtcNow() < morningReserveUntil)
            {
                morningState.InitialReleasedCount++;
            }

            await db.SaveChangesAsync(ct);
            if (scenarioTransaction is not null)
            {
                await scenarioTransaction.CommitAsync(ct);
            }
        }
        finally
        {
            if (scenarioTransaction is not null)
            {
                await scenarioTransaction.DisposeAsync();
            }
        }

        await ApplyAssignmentAsync(
            job,
            assignment,
            deal,
            dealState,
            integrationSettings,
            configuration,
            webhookUrl,
            ct);
    }

    private async Task ApplyAssignmentAsync(
        BitrixDealEventInboxEntity job,
        BitrixWorkforceAssignmentEntity assignment,
        BitrixWorkforceDeal deal,
        BitrixWorkforceDealStateEntity dealState,
        BitrixInstanceIntegrationSettings integrationSettings,
        BitrixWorkforceConfigurationEntity configuration,
        string webhookUrl,
        CancellationToken ct)
    {
        var selected = assignment.SelectedResponsibleId
                       ?? throw new InvalidOperationException("Assignment has no selected manager.");
        await db.Entry(configuration).ReloadAsync(ct);
        if (configuration.UpdatedAtUtc != assignment.ConfigurationRevisionAtUtc)
        {
            await CompleteAsync(
                job,
                "configuration_changed",
                "Distribution settings changed after this assignment was selected.",
                ct);
            return;
        }

        if (configuration.OperationMode == BitrixWorkforceDistribution.ShadowMode)
        {
            var shadowAppliedAt = UtcNow();
            assignment.DealAppliedAtUtc = shadowAppliedAt;
            assignment.ContactsAppliedAtUtc = shadowAppliedAt;
            assignment.AppliedAtUtc = shadowAppliedAt;
            assignment.Error = null;
            dealState.LastAppliedStageId = assignment.ToStageId;
            dealState.LastAppliedResponsibleId = selected;
            dealState.LastAssignmentId = assignment.Id;
            dealState.UpdatedAtUtc = shadowAppliedAt;
            await CompleteJobAsync(job, ct);
            return;
        }

        if (configuration.OperationMode != BitrixWorkforceDistribution.WriterMode
            || !configuration.WriterRulesConfirmed)
        {
            await CompleteAsync(
                job,
                "writer_disabled_or_changed",
                "Writer was disabled or its settings changed before the Bitrix24 write.",
                ct);
            return;
        }

        deal = await bitrixClient.GetDealAsync(webhookUrl, deal.Id, ct);
        await db.Entry(configuration).ReloadAsync(ct);
        if (configuration.UpdatedAtUtc != assignment.ConfigurationRevisionAtUtc
            || configuration.OperationMode != BitrixWorkforceDistribution.WriterMode
            || !configuration.WriterRulesConfirmed)
        {
            await CompleteAsync(
                job,
                "writer_disabled_or_changed_during_deal_refresh",
                "Writer was disabled or its settings changed while refreshing the deal.",
                ct);
            return;
        }

        var remoteMatchesAppliedAssignment = RemoteMatchesAppliedAssignment(
            deal,
            configuration,
            assignment,
            selected);
        var remoteMatchesOriginalDecision =
            deal.CategoryId == configuration.DealCategoryId
            && string.Equals(
                deal.StageId,
                assignment.FromStageId,
                StringComparison.OrdinalIgnoreCase)
            && deal.AssignedById == assignment.PreviousResponsibleId;
        if (assignment.DealAppliedAtUtc is null
                ? !remoteMatchesOriginalDecision && !remoteMatchesAppliedAssignment
                : !remoteMatchesAppliedAssignment)
        {
            await CompleteAsync(
                job,
                "stale_before_write",
                "Deal stage, funnel or responsible changed before the Bitrix24 write.",
                ct);
            return;
        }

        if (assignment.DealAppliedAtUtc is null)
        {
            var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.Equals(deal.StageId, assignment.ToStageId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(assignment.ToStageId))
            {
                fields["STAGE_ID"] = assignment.ToStageId;
            }

            if (deal.AssignedById != selected)
            {
                fields["ASSIGNED_BY_ID"] = selected;
            }

            AddAvitoFields(fields, deal, integrationSettings, configuration.FillOnlyEmptyAvitoFields);
            if (fields.Count > 0)
            {
                await bitrixClient.UpdateDealAsync(webhookUrl, deal.Id, fields, ct);
            }

            assignment.DealAppliedAtUtc = UtcNow();
            assignment.Error = null;
            await db.SaveChangesAsync(ct);
        }

        IReadOnlyList<long> contactIds = [];
        if (assignment.ContactsAppliedAtUtc is null)
        {
            await db.Entry(configuration).ReloadAsync(ct);
            if (configuration.UpdatedAtUtc != assignment.ConfigurationRevisionAtUtc
                || configuration.OperationMode
                != BitrixWorkforceDistribution.WriterMode
                || !configuration.WriterRulesConfirmed)
            {
                await CompleteAsync(
                    job,
                    "writer_disabled_or_changed_before_contact_sync",
                    "Writer was disabled or its settings changed before contact synchronization.",
                    ct);
                return;
            }

            if (configuration.SyncContactOwner)
            {
                var dealBeforeContactSync = await bitrixClient.GetDealAsync(
                    webhookUrl,
                    deal.Id,
                    ct);
                if (!RemoteMatchesAppliedAssignment(
                        dealBeforeContactSync,
                        configuration,
                        assignment,
                        selected))
                {
                    await CompleteAsync(
                        job,
                        "stale_before_contact_sync",
                        "Deal changed after its update; contact synchronization was cancelled.",
                        ct);
                    return;
                }

                contactIds = await bitrixClient.GetDealContactIdsAsync(webhookUrl, deal.Id, ct);
                foreach (var contactId in contactIds)
                {
                    await bitrixClient.UpdateContactOwnerAsync(
                        webhookUrl,
                        contactId,
                        selected,
                        ct);
                }
            }

            assignment.ContactId =
                contactIds.FirstOrDefault() is var first && first > 0 ? first : null;
            assignment.ContactsAppliedAtUtc = UtcNow();
            assignment.Error = null;
            await db.SaveChangesAsync(ct);
        }

        assignment.AppliedAtUtc = UtcNow();
        assignment.Error = null;
        dealState.LastAppliedStageId = assignment.ToStageId;
        dealState.LastAppliedResponsibleId = selected;
        dealState.LastAssignmentId = assignment.Id;
        dealState.UpdatedAtUtc = UtcNow();
        await CompleteJobAsync(job, ct);
    }

    private static bool RemoteMatchesAppliedAssignment(
        BitrixWorkforceDeal deal,
        BitrixWorkforceConfigurationEntity configuration,
        BitrixWorkforceAssignmentEntity assignment,
        long selected) =>
        deal.CategoryId == configuration.DealCategoryId
        && string.Equals(
            deal.StageId,
            assignment.ToStageId,
            StringComparison.OrdinalIgnoreCase)
        && deal.AssignedById == selected;

    private async Task<BitrixWorkforceMorningStateEntity> GetOrCreateMorningStateAsync(
        BitrixInstanceEntity instance,
        BitrixWorkforceConfigurationEntity configuration,
        BitrixWorkforceStageRuleEntity rule,
        IReadOnlyList<long> eligible,
        string webhookUrl,
        TimeZoneInfo timeZone,
        TimeOnly windowEnd,
        CancellationToken ct)
    {
        var now = new DateTimeOffset(UtcNow(), TimeSpan.Zero);
        var localDate = BitrixWorkforceDistribution.LocalDate(now, timeZone);
        var state = await db.BitrixWorkforceMorningStates
            .FirstOrDefaultAsync(
                x => x.BitrixInstanceId == instance.Id
                     && x.LocalDate == localDate
                     && x.Scenario == rule.Scenario,
                ct);
        if (state is not null)
        {
            return state;
        }

        var scenarioStageIds = await db.BitrixWorkforceStageRules
            .AsNoTracking()
            .Where(x => x.BitrixInstanceId == instance.Id
                        && x.IsEnabled
                        && x.Scenario == rule.Scenario)
            .Select(x => x.SourceStageId)
            .ToListAsync(ct);
        var queuedDeals = await bitrixClient.ListDealsAsync(
            webhookUrl,
            configuration.DealCategoryId,
            scenarioStageIds,
            ct);

        var localEnd = localDate.ToDateTime(windowEnd, DateTimeKind.Unspecified);
        var windowEndUtc = new DateTimeOffset(
                localEnd,
                timeZone.GetUtcOffset(localEnd))
            .UtcDateTime;
        var reserveUntil = Min(
            UtcNow().AddMinutes(configuration.LateJoinReserveMinutes),
            windowEndUtc);
        state = new BitrixWorkforceMorningStateEntity
        {
            BitrixInstanceId = instance.Id,
            LocalDate = localDate,
            Scenario = rule.Scenario,
            FirstManagerId = eligible[0],
            FirstManagerSeenAtUtc = UtcNow(),
            ReserveUntilUtc = reserveUntil,
            InitialReleaseLimit = BitrixWorkforceDistribution.CalculateInitialReleaseCount(
                queuedDeals.Count,
                configuration.SingleManagerInitialReleasePercent),
            InitialReleasedCount = 0
        };
        db.BitrixWorkforceMorningStates.Add(state);
        await db.SaveChangesAsync(ct);
        return state;
    }

    private async Task<BitrixWorkforceDealStateEntity> GetOrCreateDealStateAsync(
        Guid instanceId,
        long dealId,
        CancellationToken ct)
    {
        var state = await db.BitrixWorkforceDealStates
            .FirstOrDefaultAsync(
                x => x.BitrixInstanceId == instanceId && x.DealId == dealId,
                ct);
        if (state is not null)
        {
            return state;
        }

        state = new BitrixWorkforceDealStateEntity
        {
            BitrixInstanceId = instanceId,
            DealId = dealId,
            UpdatedAtUtc = UtcNow()
        };
        db.BitrixWorkforceDealStates.Add(state);
        return state;
    }

    private async Task DeferWithDecisionAsync(
        BitrixDealEventInboxEntity job,
        BitrixWorkforceAssignmentEntity? assignment,
        BitrixWorkforceStageRuleEntity rule,
        BitrixWorkforceDeal deal,
        BitrixWorkforceConfigurationEntity configuration,
        DateTime nextAttemptAtUtc,
        string reason,
        CancellationToken ct,
        string decision = BitrixWorkforceDecisions.Deferred)
    {
        UpsertAssignment(
            job,
            assignment,
            rule,
            deal,
            configuration,
            decision,
            reason,
            selectedManagerId: null);
        job.State = BitrixWorkforceInboxStates.Pending;
        job.NextAttemptAtUtc = nextAttemptAtUtc;
        job.LockOwner = null;
        job.LockedUntilUtc = null;
        job.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task CompleteWithDecisionAsync(
        BitrixDealEventInboxEntity job,
        BitrixWorkforceAssignmentEntity? assignment,
        BitrixWorkforceStageRuleEntity rule,
        BitrixWorkforceDeal deal,
        BitrixWorkforceConfigurationEntity configuration,
        string decision,
        string reason,
        long? selectedManagerId,
        CancellationToken ct)
    {
        UpsertAssignment(
            job,
            assignment,
            rule,
            deal,
            configuration,
            decision,
            reason,
            selectedManagerId);
        await CompleteJobAsync(job, ct);
    }

    private BitrixWorkforceAssignmentEntity UpsertAssignment(
        BitrixDealEventInboxEntity job,
        BitrixWorkforceAssignmentEntity? assignment,
        BitrixWorkforceStageRuleEntity rule,
        BitrixWorkforceDeal deal,
        BitrixWorkforceConfigurationEntity configuration,
        string decision,
        string reason,
        long? selectedManagerId)
    {
        if (assignment is null)
        {
            assignment = new BitrixWorkforceAssignmentEntity
            {
                Id = Guid.NewGuid(),
                InboxId = job.Id,
                BitrixInstanceId = job.BitrixInstanceId,
                DealId = job.DealId,
                CreatedAtUtc = UtcNow()
            };
            db.BitrixWorkforceAssignments.Add(assignment);
        }

        if (decision == BitrixWorkforceDecisions.Assigned
            && assignment.Decision != BitrixWorkforceDecisions.Assigned)
        {
            assignment.CreatedAtUtc = UtcNow();
        }

        assignment.Scenario = rule.Scenario;
        assignment.OperationMode = configuration.OperationMode;
        assignment.FromStageId = deal.StageId;
        assignment.ToStageId = rule.TargetStageId;
        assignment.PreviousResponsibleId = deal.AssignedById;
        assignment.SelectedResponsibleId = selectedManagerId;
        assignment.Decision = decision;
        assignment.Reason = reason;
        assignment.ConfigurationRevisionAtUtc = configuration.UpdatedAtUtc;
        assignment.Error = null;
        return assignment;
    }

    private async Task CompleteAsync(
        BitrixDealEventInboxEntity job,
        string reasonCode,
        string reason,
        CancellationToken ct)
    {
        var existing = await db.BitrixWorkforceAssignments
            .FirstOrDefaultAsync(x => x.InboxId == job.Id, ct);
        if (existing is null)
        {
            db.BitrixWorkforceAssignments.Add(new BitrixWorkforceAssignmentEntity
            {
                Id = Guid.NewGuid(),
                InboxId = job.Id,
                BitrixInstanceId = job.BitrixInstanceId,
                DealId = job.DealId,
                Scenario = reasonCode,
                OperationMode = BitrixWorkforceDistribution.DisabledMode,
                Decision = BitrixWorkforceDecisions.Ignored,
                Reason = reason,
                CreatedAtUtc = UtcNow()
            });
        }
        else if (existing.AppliedAtUtc is null)
        {
            existing.Decision = BitrixWorkforceDecisions.Ignored;
            existing.Reason = reason;
            existing.Error = null;
        }

        await CompleteJobAsync(job, ct);
    }

    private async Task CompleteJobAsync(
        BitrixDealEventInboxEntity job,
        CancellationToken ct)
    {
        job.State = BitrixWorkforceInboxStates.Completed;
        job.CompletedAtUtc = UtcNow();
        job.LockOwner = null;
        job.LockedUntilUtc = null;
        job.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task HandleFailureAsync(
        BitrixDealEventInboxEntity job,
        Exception exception,
        CancellationToken ct)
    {
        var configuration = await db.BitrixWorkforceConfigurations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.BitrixInstanceId == job.BitrixInstanceId, ct);
        var retryDelay = configuration?.RetryDelaySeconds ?? 60;
        var maxAttempts = configuration?.MaxAttempts ?? 20;
        job.FailureCount++;
        job.LastError = Truncate(exception.Message, 2000);
        job.LockOwner = null;
        job.LockedUntilUtc = null;

        var assignment = await db.BitrixWorkforceAssignments
            .FirstOrDefaultAsync(x => x.InboxId == job.Id, ct);
        if (assignment is not null)
        {
            assignment.Error = job.LastError;
        }

        if (job.FailureCount >= maxAttempts)
        {
            job.State = BitrixWorkforceInboxStates.Dead;
            job.CompletedAtUtc = UtcNow();
            if (assignment is not null)
            {
                assignment.Decision = BitrixWorkforceDecisions.Failed;
            }
        }
        else
        {
            job.State = BitrixWorkforceInboxStates.Pending;
            job.NextAttemptAtUtc = UtcNow().AddSeconds(retryDelay);
        }

        await db.SaveChangesAsync(ct);
    }

    private static void AddAvitoFields(
        IDictionary<string, object?> fields,
        BitrixWorkforceDeal deal,
        BitrixInstanceIntegrationSettings settings,
        bool fillOnlyEmpty)
    {
        var parsed = BitrixAvitoCommentParser.Parse(deal.Comments);
        AddIfAllowed(
            fields,
            deal,
            settings.DealAgeUfCode,
            parsed.Age?.ToString(CultureInfo.InvariantCulture),
            fillOnlyEmpty);
        AddIfAllowed(
            fields,
            deal,
            settings.DealProfessionUfCode,
            parsed.Profession,
            fillOnlyEmpty);
        AddIfAllowed(
            fields,
            deal,
            settings.DealCityUfCode,
            parsed.City,
            fillOnlyEmpty);
    }

    private static void AddIfAllowed(
        IDictionary<string, object?> fields,
        BitrixWorkforceDeal deal,
        string fieldCode,
        string? value,
        bool fillOnlyEmpty)
    {
        var code = fieldCode?.Trim() ?? string.Empty;
        if (code.Length == 0 || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (fillOnlyEmpty
            && deal.Fields.TryGetValue(code, out var existing)
            && !string.IsNullOrWhiteSpace(existing)
            && existing != "0")
        {
            return;
        }

        fields[code] = value.Trim();
    }

    private static bool IsManagerCreatedDeal(
        BitrixWorkforceDeal deal,
        IReadOnlyCollection<long> managerIds,
        string idempotencyFieldCode)
    {
        if (deal.CreatedById is not long creatorId || !managerIds.Contains(creatorId))
        {
            return false;
        }

        var code = idempotencyFieldCode?.Trim() ?? string.Empty;
        return code.Length == 0
               || !deal.Fields.TryGetValue(code, out var marker)
               || string.IsNullOrWhiteSpace(marker);
    }

    private static string BuildReconciliationEventKey(
        Guid instanceId,
        long dealId,
        string stageId,
        string revision,
        DateOnly localDate)
    {
        var canonical = string.Join(
            '|',
            "RECONCILE",
            instanceId.ToString("N"),
            dealId.ToString(CultureInfo.InvariantCulture),
            stageId.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(revision)
                ? localDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                : revision.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<IReadOnlyList<BitrixWorkforceManagerStatus>> GetManagerStatusesAsync(
        Guid bitrixInstanceId,
        string webhookUrl,
        IReadOnlyList<long> managerIds,
        CancellationToken ct)
    {
        var signature = string.Join(
            ',',
            managerIds.Where(x => x > 0).Distinct());
        var now = UtcNow();
        if (_managerStatusCache.TryGetValue(bitrixInstanceId, out var cached)
            && cached.ManagerSignature == signature
            && now - cached.CachedAtUtc <= TimeSpan.FromSeconds(15))
        {
            return cached.Statuses;
        }

        var statuses = await bitrixClient.GetManagerStatusesAsync(
            webhookUrl,
            managerIds,
            ct);
        _managerStatusCache[bitrixInstanceId] = new ManagerStatusCacheEntry(
            signature,
            now,
            statuses);
        return statuses;
    }

    private async Task<List<BitrixDealEventInboxEntity>> LeaseJobsAsync(
        DateTime now,
        DateTime leaseUntil,
        string workerId,
        int batchSize,
        CancellationToken ct)
    {
        if (!UsesPostgres())
        {
            var fallbackJobs = await DueJobs(now)
                .Take(batchSize)
                .ToListAsync(ct);
            MarkLeased(fallbackJobs, leaseUntil, workerId);
            if (fallbackJobs.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            return fallbackJobs;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var jobs = await db.BitrixDealEventInbox
            .FromSqlInterpolated(
                $"""
                 SELECT *
                 FROM "BitrixDealEventInbox"
                 WHERE (
                     "State" = {BitrixWorkforceInboxStates.Pending}
                     AND "NextAttemptAtUtc" <= {now}
                 ) OR (
                     "State" = {BitrixWorkforceInboxStates.Processing}
                     AND "LockedUntilUtc" IS NOT NULL
                     AND "LockedUntilUtc" <= {now}
                 )
                 ORDER BY "NextAttemptAtUtc", "Id"
                 FOR UPDATE SKIP LOCKED
                 LIMIT {batchSize}
                 """)
            .ToListAsync(ct);
        MarkLeased(jobs, leaseUntil, workerId);
        if (jobs.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return jobs;
    }

    private IOrderedQueryable<BitrixDealEventInboxEntity> DueJobs(DateTime now) =>
        db.BitrixDealEventInbox
            .Where(x =>
                (x.State == BitrixWorkforceInboxStates.Pending && x.NextAttemptAtUtc <= now)
                || (x.State == BitrixWorkforceInboxStates.Processing
                    && x.LockedUntilUtc != null
                    && x.LockedUntilUtc <= now))
            .OrderBy(x => x.NextAttemptAtUtc)
            .ThenBy(x => x.Id);

    private static void MarkLeased(
        IEnumerable<BitrixDealEventInboxEntity> jobs,
        DateTime leaseUntil,
        string workerId)
    {
        foreach (var job in jobs)
        {
            job.State = BitrixWorkforceInboxStates.Processing;
            job.LockOwner = workerId;
            job.LockedUntilUtc = leaseUntil;
            job.AttemptCount++;
        }
    }

    private async Task<IAsyncDisposable> AcquireDealLockAsync(
        Guid bitrixInstanceId,
        long dealId,
        CancellationToken ct)
    {
        if (!UsesPostgres())
        {
            return NoopAsyncDisposable.Instance;
        }

        var connection = db.Database.GetDbConnection();
        var shouldCloseConnection = connection.State != ConnectionState.Open;
        if (shouldCloseConnection)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        var lockKey = BuildAdvisoryLockKey(
            $"deal|{bitrixInstanceId:N}|{dealId.ToString(CultureInfo.InvariantCulture)}");
        try
        {
            await ExecuteAdvisoryLockCommandAsync(
                connection,
                "SELECT pg_advisory_lock(@lockKey)",
                lockKey,
                ct);
        }
        catch
        {
            if (shouldCloseConnection)
            {
                await db.Database.CloseConnectionAsync();
            }

            throw;
        }

        return new AsyncRelease(async () =>
        {
            try
            {
                await ExecuteAdvisoryLockCommandAsync(
                    connection,
                    "SELECT pg_advisory_unlock(@lockKey)",
                    lockKey,
                    CancellationToken.None);
            }
            finally
            {
                if (shouldCloseConnection)
                {
                    await db.Database.CloseConnectionAsync();
                }
            }
        });
    }

    private static async Task ExecuteAdvisoryLockCommandAsync(
        System.Data.Common.DbConnection connection,
        string commandText,
        long lockKey,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lockKey";
        parameter.Value = lockKey;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<IDbContextTransaction?> BeginScenarioTransactionAsync(
        Guid bitrixInstanceId,
        string scenario,
        CancellationToken ct)
    {
        if (!UsesPostgres())
        {
            return null;
        }

        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var lockKey = BuildScenarioLockKey(bitrixInstanceId, scenario);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({lockKey})",
                ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private bool UsesPostgres() =>
        string.Equals(
            db.Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            StringComparison.Ordinal);

    private static long BuildScenarioLockKey(Guid bitrixInstanceId, string scenario)
    {
        var canonical =
            $"scenario|{bitrixInstanceId:N}|{scenario.Trim().ToLowerInvariant()}";
        return BuildAdvisoryLockKey(canonical);
    }

    private static long BuildAdvisoryLockKey(string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return BitConverter.ToInt64(hash, 0);
    }

    private sealed class AsyncRelease(Func<ValueTask> release) : IAsyncDisposable
    {
        private Func<ValueTask>? _release = release;

        public async ValueTask DisposeAsync()
        {
            var action = Interlocked.Exchange(ref _release, null);
            if (action is not null)
            {
                await action();
            }
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static NoopAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record ManagerStatusCacheEntry(
        string ManagerSignature,
        DateTime CachedAtUtc,
        IReadOnlyList<BitrixWorkforceManagerStatus> Statuses);

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private static DateTime Min(DateTime left, DateTime right) =>
        left <= right ? left : right;

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

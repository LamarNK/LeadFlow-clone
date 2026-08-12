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
                    x => x.Id == configuration.BitrixInstanceId
                         && x.IsEnabled
                         && x.DeletedAtUtc == null,
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
                        localDate,
                        configuration.UpdatedAtUtc);
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
            await PreserveExistingAssignmentGuardAsync(job, ct);
            await CompleteAsync(job, "disabled", "Workforce distribution is disabled.", ct);
            return;
        }

        var webhookUrl = await bitrixInstances.ResolveWebhookUrlAsync(instance, ct);
        var canonicalPortalHost = BitrixWebhookValidator.TryGetPortalHost(webhookUrl);
        if (configuration.OperationMode == BitrixWorkforceDistribution.WriterMode
            && (string.IsNullOrWhiteSpace(canonicalPortalHost)
                || await HasCompetingWriterAsync(
                    instance,
                    configuration,
                    canonicalPortalHost,
                    ct)))
        {
            await PreserveExistingAssignmentGuardAsync(job, ct);
            configuration.OperationMode = BitrixWorkforceDistribution.DisabledMode;
            configuration.WriterRulesConfirmed = false;
            configuration.UpdatedAtUtc = UtcNow();
            await CompleteAsync(
                job,
                "duplicate_writer_configuration",
                "Writer was disabled because its Bitrix24 connection is unsafe or another enabled Orbita writer targets the same portal and funnel.",
                ct);
            return;
        }

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

        var sourceRule = deal.CategoryId == configuration.DealCategoryId
            ? rules.FirstOrDefault(x =>
                string.Equals(x.SourceStageId, deal.StageId, StringComparison.OrdinalIgnoreCase))
            : null;
        var observedScenario = ResolveObservedScenario(
            deal,
            configuration,
            sourceRule,
            rules,
            dealState,
            previouslyObservedStageId);
        ObserveScenario(dealState, observedScenario);
        var rule = sourceRule is not null
                   && string.Equals(
                       sourceRule.Scenario,
                       observedScenario,
                       StringComparison.Ordinal)
            ? sourceRule
            : null;
        var sourceScenarioWasRemapped = sourceRule is not null
                                        && rule is null
                                        && !string.IsNullOrWhiteSpace(dealState.ActiveScenario)
                                        && !rules.Any(x =>
                                            string.Equals(
                                                x.Scenario,
                                                dealState.ActiveScenario,
                                                StringComparison.Ordinal)
                                            && (string.Equals(
                                                    x.SourceStageId,
                                                    deal.StageId,
                                                    StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(
                                                    x.TargetStageId,
                                                    deal.StageId,
                                                    StringComparison.OrdinalIgnoreCase)));

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
            if (EnsureAssignmentScenarioHandled(
                    dealState,
                    assignment,
                    deal,
                    configuration,
                    UtcNow()))
            {
                await db.SaveChangesAsync(ct);
            }
        }

        if (assignment is not null
            && configuration.UpdatedAtUtc != assignment.ConfigurationRevisionAtUtc)
        {
            if (string.Equals(
                    dealState.ActiveScenario,
                    assignment.Scenario,
                    StringComparison.Ordinal))
            {
                MarkScenarioHandled(
                    dealState,
                    assignment.Scenario,
                    assignment.OperationMode,
                    UtcNow());
            }

            await CompleteAsync(
                job,
                "configuration_changed",
                "Distribution settings changed after this decision was created.",
                ct);
            return;
        }

        if (assignment?.Decision == BitrixWorkforceDecisions.Assigned
            && assignment.SelectedResponsibleId is > 0)
        {
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
                    instance,
                    configuration,
                    webhookUrl,
                    ct);
                return;
            }

            await CompleteAsync(
                job,
                assignment.DealAppliedAtUtc is not null
                    ? "stale_partial_assignment"
                    : "stale_selected_assignment",
                assignment.DealAppliedAtUtc is not null
                    ? "Deal or responsible changed after the deal step; contact synchronization was cancelled."
                    : "Deal or responsible changed after a manager was selected; the current state is preserved.",
                ct);
            return;
        }

        if (sourceScenarioWasRemapped)
        {
            var handledAt = UtcNow() < configuration.UpdatedAtUtc
                ? configuration.UpdatedAtUtc
                : UtcNow();
            MarkScenarioHandled(
                dealState,
                sourceRule!.Scenario,
                configuration.OperationMode,
                handledAt);
            await CompleteWithDecisionAsync(
                job,
                assignment,
                sourceRule,
                deal,
                configuration,
                BitrixWorkforceDecisions.Ignored,
                "The source stage was remapped to another scenario; the current responsible is preserved.",
                selectedManagerId: null,
                ct);
            return;
        }

        if (rule is null)
        {
            await CompleteAsync(job, "stage_not_configured", "Deal stage is not configured.", ct);
            return;
        }

        var unprotectedPriorFailures = assignment is null
            ? await db.BitrixDealEventInbox
                .Where(prior => prior.BitrixInstanceId == job.BitrixInstanceId
                                && prior.DealId == job.DealId
                                && prior.Id != job.Id
                                && prior.FailureCount > 0
                                && !db.BitrixWorkforceAssignments.Any(
                                    decision => decision.InboxId == prior.Id))
                .ToListAsync(ct)
            : [];
        if (assignment is null
            && (job.FailureCount > 0 || unprotectedPriorFailures.Count > 0))
        {
            foreach (var priorFailure in unprotectedPriorFailures)
            {
                UpsertAssignment(
                    priorFailure,
                    null,
                    rule,
                    deal,
                    configuration,
                    BitrixWorkforceDecisions.Ignored,
                    "Failure tombstone: the original owner was not captured.",
                    selectedManagerId: null);
            }

            var handledAt = UtcNow() < configuration.UpdatedAtUtc
                ? configuration.UpdatedAtUtc
                : UtcNow();
            MarkScenarioHandled(
                dealState,
                rule.Scenario,
                configuration.OperationMode,
                handledAt);
            await CompleteWithDecisionAsync(
                job,
                assignment,
                rule,
                deal,
                configuration,
                BitrixWorkforceDecisions.Ignored,
                "A previous attempt failed before the original owner could be recorded; the current owner is preserved.",
                selectedManagerId: null,
                ct);
            return;
        }

        var scenarioHandledAt = GetScenarioHandledAt(dealState, configuration.OperationMode);
        if (scenarioHandledAt is not null
            && string.Equals(
                dealState.ActiveScenario,
                rule.Scenario,
                StringComparison.Ordinal))
        {
            // Refresh the coverage marker under the currently saved configuration.
            // This lets a shadow reconcile prove that an already protected deal was
            // observed after the latest settings change without making a CRM write.
            if (configuration.OperationMode == BitrixWorkforceDistribution.ShadowMode)
            {
                var handledAt = UtcNow() < configuration.UpdatedAtUtc
                    ? configuration.UpdatedAtUtc
                    : UtcNow();
                MarkScenarioHandled(
                    dealState,
                    rule.Scenario,
                    configuration.OperationMode,
                    handledAt);
            }
            var stateMatchesLastDecision = string.Equals(
                                               dealState.LastAppliedStageId,
                                               deal.StageId,
                                               StringComparison.OrdinalIgnoreCase)
                                           && (configuration.OperationMode
                                               == BitrixWorkforceDistribution.ShadowMode
                                               ? string.Equals(
                                                   previouslyObservedStageId,
                                                   deal.StageId,
                                                   StringComparison.OrdinalIgnoreCase)
                                               : dealState.LastAppliedResponsibleId
                                                 == deal.AssignedById);
            await CompleteAsync(
                job,
                stateMatchesLastDecision ? "self_update" : "scenario_already_handled",
                stateMatchesLastDecision
                    ? "State already matches the last applied decision."
                    : "This scenario entry was already handled; the current responsible is preserved.",
                ct);
            return;
        }

        if (assignment is null)
        {
            var priorActiveAssignment = await db.BitrixWorkforceAssignments
                .Join(
                    db.BitrixDealEventInbox,
                    prior => prior.InboxId,
                    inbox => inbox.Id,
                    (prior, inbox) => new { Assignment = prior, Inbox = inbox })
                .Where(x => x.Assignment.BitrixInstanceId == instance.Id
                            && x.Assignment.DealId == deal.Id
                            && x.Assignment.Scenario == rule.Scenario
                            && x.Inbox.Id != job.Id
                            && (x.Inbox.State == BitrixWorkforceInboxStates.Pending
                                || x.Inbox.State == BitrixWorkforceInboxStates.Processing)
                            && (x.Assignment.Decision == BitrixWorkforceDecisions.Deferred
                                || x.Assignment.Decision == BitrixWorkforceDecisions.Reserved
                                || x.Assignment.Decision == BitrixWorkforceDecisions.Assigned))
                .OrderBy(x => x.Assignment.CreatedAtUtc)
                .Select(x => x.Assignment)
                .FirstOrDefaultAsync(ct);
            if (priorActiveAssignment is not null)
            {
                if (priorActiveAssignment.PreviousResponsibleId != deal.AssignedById)
                {
                    MarkScenarioHandled(
                        dealState,
                        rule.Scenario,
                        priorActiveAssignment.OperationMode,
                        UtcNow());
                    dealState.LastAssignmentId = priorActiveAssignment.Id;
                }

                await CompleteAsync(
                    job,
                    "another_event_active",
                    "Another event already owns this scenario entry; its original-owner baseline is preserved.",
                    ct);
                return;
            }
        }

        if (assignment is not null
            && (assignment.Decision is BitrixWorkforceDecisions.Deferred
                or BitrixWorkforceDecisions.Reserved))
        {
            if (!string.Equals(
                    assignment.Scenario,
                    rule.Scenario,
                    StringComparison.Ordinal))
            {
                await CompleteAsync(
                    job,
                    "stale_deferred_assignment",
                    "The deal entered another scenario while this decision was waiting.",
                    ct);
                return;
            }

            if (assignment.PreviousResponsibleId != deal.AssignedById)
            {
                MarkScenarioHandled(
                    dealState,
                    rule.Scenario,
                    assignment.OperationMode,
                    UtcNow());
                await CompleteAsync(
                    job,
                    "responsible_changed_while_waiting",
                    "The responsible changed while distribution was waiting; the current responsible is preserved.",
                    ct);
                return;
            }
        }

        if (rule.Scenario == BitrixWorkforceDistribution.NewScenario
            && configuration.PreserveManualNewOwner
            && previouslyObservedStageId is null
            && ShouldPreserveExistingNewOwner(
                deal,
                managerIds,
                integrationSettings.ResponsibleId,
                integrationSettings.DealIdempotencyUfCode))
        {
            MarkScenarioHandled(
                dealState,
                rule.Scenario,
                configuration.OperationMode,
                UtcNow());
            await CompleteWithDecisionAsync(
                job,
                assignment,
                rule,
                deal,
                configuration,
                BitrixWorkforceDecisions.Ignored,
                "NEW already has a non-default or manually selected responsible; the current owner is preserved.",
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

        if (assignment is null)
        {
            assignment = UpsertAssignment(
                job,
                assignment,
                rule,
                deal,
                configuration,
                BitrixWorkforceDecisions.Deferred,
                "Distribution evaluation started.",
                selectedManagerId: null);
            // Persist the original owner before any external manager-status request.
            // A retry can then detect and preserve a manual owner change.
            await db.SaveChangesAsync(ct);
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
            var lockedConfiguration = await LockConfigurationForSelectionAsync(
                instance.Id,
                ct);
            var configurationStillMatchesDecision = lockedConfiguration is not null
                                                    && lockedConfiguration.UpdatedAtUtc
                                                    == assignment.ConfigurationRevisionAtUtc
                                                    && string.Equals(
                                                        lockedConfiguration.OperationMode,
                                                        assignment.OperationMode,
                                                        StringComparison.Ordinal)
                                                    && (lockedConfiguration.OperationMode
                                                        != BitrixWorkforceDistribution.WriterMode
                                                        || lockedConfiguration.WriterRulesConfirmed);
            if (!configurationStillMatchesDecision)
            {
                MarkScenarioHandled(
                    dealState,
                    rule.Scenario,
                    assignment.OperationMode,
                    UtcNow());
                await CompleteAsync(
                    job,
                    "configuration_changed_before_selection",
                    "Distribution settings changed while manager availability was being evaluated.",
                    ct);
                if (scenarioTransaction is not null)
                {
                    await scenarioTransaction.CommitAsync(ct);
                }

                return;
            }

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
                    x => x.BitrixInstanceId == instance.Id
                         && x.Scenario == rule.Scenario
                         && x.OperationMode == configuration.OperationMode,
                    ct);
            if (cursor is null)
            {
                cursor = new BitrixWorkforceCursorEntity
                {
                    BitrixInstanceId = instance.Id,
                    Scenario = rule.Scenario,
                    OperationMode = configuration.OperationMode
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
            var selectedAt = UtcNow();
            cursor.LastAssignedBitrixUserId = selected;
            cursor.LastAssignedAtUtc = selectedAt;
            MarkScenarioHandled(
                dealState,
                rule.Scenario,
                configuration.OperationMode,
                selectedAt);
            dealState.LastAssignmentId = assignment.Id;
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
            instance,
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
        BitrixInstanceEntity instance,
        BitrixWorkforceConfigurationEntity configuration,
        string webhookUrl,
        CancellationToken ct)
    {
        var selected = assignment.SelectedResponsibleId
                       ?? throw new InvalidOperationException("Assignment has no selected manager.");
        await using var settingsWriteTransaction = await BeginSettingsWriteTransactionAsync(
            instance.Id,
            ct);
        await db.Entry(configuration).ReloadAsync(ct);
        await db.Entry(instance).ReloadAsync(ct);
        var instanceRevisionAtUtc = instance.UpdatedAtUtc;

        async Task CompleteAndCommitAsync(string reasonCode, string reason)
        {
            await CompleteAsync(job, reasonCode, reason, ct);
            if (settingsWriteTransaction is not null)
            {
                await settingsWriteTransaction.CommitAsync(ct);
            }
        }

        if (!instance.IsEnabled
            || configuration.UpdatedAtUtc != assignment.ConfigurationRevisionAtUtc)
        {
            await CompleteAndCommitAsync(
                "configuration_changed",
                "The Bitrix24 connection or distribution settings changed after this assignment was selected.");
            return;
        }

        if (configuration.OperationMode == BitrixWorkforceDistribution.ShadowMode)
        {
            var shadowAppliedAt = UtcNow();
            assignment.DealAppliedAtUtc = shadowAppliedAt;
            assignment.ContactsAppliedAtUtc = shadowAppliedAt;
            assignment.AppliedAtUtc = shadowAppliedAt;
            assignment.Error = null;
            MarkScenarioHandled(
                dealState,
                assignment.Scenario,
                BitrixWorkforceDistribution.ShadowMode,
                shadowAppliedAt);
            dealState.LastAppliedStageId = assignment.ToStageId;
            dealState.LastAppliedResponsibleId = selected;
            dealState.LastAssignmentId = assignment.Id;
            dealState.UpdatedAtUtc = shadowAppliedAt;
            await CompleteJobAsync(job, ct);
            if (settingsWriteTransaction is not null)
            {
                await settingsWriteTransaction.CommitAsync(ct);
            }
            return;
        }

        if (configuration.OperationMode != BitrixWorkforceDistribution.WriterMode
            || !configuration.WriterRulesConfirmed)
        {
            await CompleteAndCommitAsync(
                "writer_disabled_or_changed",
                "Writer was disabled or its settings changed before the Bitrix24 write.");
            return;
        }

        deal = await bitrixClient.GetDealAsync(webhookUrl, deal.Id, ct);
        await db.Entry(configuration).ReloadAsync(ct);
        await db.Entry(instance).ReloadAsync(ct);
        if (!instance.IsEnabled
            || instance.UpdatedAtUtc != instanceRevisionAtUtc
            || configuration.UpdatedAtUtc != assignment.ConfigurationRevisionAtUtc
            || configuration.OperationMode != BitrixWorkforceDistribution.WriterMode
            || !configuration.WriterRulesConfirmed)
        {
            await CompleteAndCommitAsync(
                "writer_disabled_or_changed_during_deal_refresh",
                "Writer was disabled or its settings changed while refreshing the deal.");
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
            await CompleteAndCommitAsync(
                "stale_before_write",
                "Deal stage, funnel or responsible changed before the Bitrix24 write.");
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

            var dealAppliedAt = UtcNow();
            assignment.DealAppliedAtUtc = dealAppliedAt;
            assignment.Error = null;
            MarkScenarioHandled(
                dealState,
                assignment.Scenario,
                BitrixWorkforceDistribution.WriterMode,
                dealAppliedAt);
            dealState.LastAppliedStageId = assignment.ToStageId;
            dealState.LastAppliedResponsibleId = selected;
            dealState.LastAssignmentId = assignment.Id;
            dealState.UpdatedAtUtc = dealAppliedAt;
            await db.SaveChangesAsync(ct);
        }

        IReadOnlyList<long> contactIds = [];
        if (assignment.ContactsAppliedAtUtc is null)
        {
            await db.Entry(configuration).ReloadAsync(ct);
            await db.Entry(instance).ReloadAsync(ct);
            if (!instance.IsEnabled
                || instance.UpdatedAtUtc != instanceRevisionAtUtc
                || configuration.UpdatedAtUtc != assignment.ConfigurationRevisionAtUtc
                || configuration.OperationMode
                != BitrixWorkforceDistribution.WriterMode
                || !configuration.WriterRulesConfirmed)
            {
                await CompleteAndCommitAsync(
                    "writer_disabled_or_changed_before_contact_sync",
                    "Writer was disabled or its settings changed before contact synchronization.");
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
                    await CompleteAndCommitAsync(
                        "stale_before_contact_sync",
                        "Deal changed after its update; contact synchronization was cancelled.");
                    return;
                }

                contactIds = await bitrixClient.GetDealContactIdsAsync(webhookUrl, deal.Id, ct);
                foreach (var contactId in contactIds)
                {
                    var currentContactOwner = await bitrixClient.GetContactOwnerIdAsync(
                        webhookUrl,
                        contactId,
                        ct);
                    if (currentContactOwner == selected)
                    {
                        continue;
                    }

                    if (currentContactOwner != assignment.PreviousResponsibleId)
                    {
                        logger.LogInformation(
                            "Bitrix contact {ContactId} owner {OwnerId} was preserved while assigning deal {DealId}; expected baseline owner {BaselineOwnerId}.",
                            contactId,
                            currentContactOwner,
                            deal.Id,
                            assignment.PreviousResponsibleId);
                        continue;
                    }

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
        dealState.UpdatedAtUtc = UtcNow();
        await CompleteJobAsync(job, ct);
        if (settingsWriteTransaction is not null)
        {
            await settingsWriteTransaction.CommitAsync(ct);
        }
    }

    private static string? ResolveObservedScenario(
        BitrixWorkforceDeal deal,
        BitrixWorkforceConfigurationEntity configuration,
        BitrixWorkforceStageRuleEntity? sourceRule,
        IReadOnlyCollection<BitrixWorkforceStageRuleEntity> rules,
        BitrixWorkforceDealStateEntity dealState,
        string? previouslyObservedStageId)
    {
        if (deal.CategoryId != configuration.DealCategoryId)
        {
            return null;
        }

        var activeScenario = dealState.ActiveScenario;
        if (!string.IsNullOrWhiteSpace(activeScenario)
            && string.Equals(
                previouslyObservedStageId,
                deal.StageId,
                StringComparison.OrdinalIgnoreCase))
        {
            // A settings edit must not look like the deal left and re-entered a scenario.
            // If Bitrix still reports the exact same stage, retain the existing entry guard.
            return activeScenario;
        }

        if (!string.IsNullOrWhiteSpace(activeScenario)
            && rules.Any(x =>
                string.Equals(x.Scenario, activeScenario, StringComparison.Ordinal)
                && (string.Equals(
                        x.SourceStageId,
                        deal.StageId,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        x.TargetStageId,
                        deal.StageId,
                        StringComparison.OrdinalIgnoreCase))))
        {
            return activeScenario;
        }

        return sourceRule?.Scenario;
    }

    private void ObserveScenario(
        BitrixWorkforceDealStateEntity dealState,
        string? observedScenario)
    {
        var normalized = string.IsNullOrWhiteSpace(observedScenario)
            ? null
            : observedScenario.Trim();
        if (string.Equals(dealState.ActiveScenario, normalized, StringComparison.Ordinal))
        {
            return;
        }

        dealState.ActiveScenario = normalized;
        dealState.ActiveScenarioShadowHandledAtUtc = null;
        dealState.ActiveScenarioWriterHandledAtUtc = null;
        dealState.UpdatedAtUtc = UtcNow();
    }

    private static DateTime? GetScenarioHandledAt(
        BitrixWorkforceDealStateEntity dealState,
        string operationMode) =>
        operationMode switch
        {
            BitrixWorkforceDistribution.ShadowMode =>
                dealState.ActiveScenarioShadowHandledAtUtc
                ?? dealState.ActiveScenarioWriterHandledAtUtc,
            BitrixWorkforceDistribution.WriterMode =>
                dealState.ActiveScenarioWriterHandledAtUtc
                ?? dealState.ActiveScenarioShadowHandledAtUtc,
            _ => null
        };

    private void MarkScenarioHandled(
        BitrixWorkforceDealStateEntity dealState,
        string scenario,
        string operationMode,
        DateTime handledAtUtc)
    {
        if (!string.Equals(dealState.ActiveScenario, scenario, StringComparison.Ordinal))
        {
            dealState.ActiveScenario = scenario;
            dealState.ActiveScenarioShadowHandledAtUtc = null;
            dealState.ActiveScenarioWriterHandledAtUtc = null;
        }

        if (operationMode == BitrixWorkforceDistribution.ShadowMode)
        {
            if (dealState.ActiveScenarioShadowHandledAtUtc is not DateTime shadowHandledAt
                || shadowHandledAt < handledAtUtc)
            {
                dealState.ActiveScenarioShadowHandledAtUtc = handledAtUtc;
            }
        }
        else if (operationMode == BitrixWorkforceDistribution.WriterMode)
        {
            if (dealState.ActiveScenarioWriterHandledAtUtc is not DateTime writerHandledAt
                || writerHandledAt < handledAtUtc)
            {
                dealState.ActiveScenarioWriterHandledAtUtc = handledAtUtc;
            }
        }

        if (dealState.UpdatedAtUtc < handledAtUtc)
        {
            dealState.UpdatedAtUtc = handledAtUtc;
        }
    }

    private bool EnsureAssignmentScenarioHandled(
        BitrixWorkforceDealStateEntity dealState,
        BitrixWorkforceAssignmentEntity assignment,
        BitrixWorkforceDeal deal,
        BitrixWorkforceConfigurationEntity configuration,
        DateTime handledAtUtc)
    {
        var belongsToCurrentScenario = string.Equals(
                                           dealState.ActiveScenario,
                                           assignment.Scenario,
                                           StringComparison.Ordinal)
                                       || (string.IsNullOrWhiteSpace(dealState.ActiveScenario)
                                           && deal.CategoryId == configuration.DealCategoryId
                                           && (string.Equals(
                                                   deal.StageId,
                                                   assignment.FromStageId,
                                                   StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(
                                                   deal.StageId,
                                                   assignment.ToStageId,
                                                   StringComparison.OrdinalIgnoreCase)));
        if (!belongsToCurrentScenario)
        {
            return false;
        }

        var previousScenario = dealState.ActiveScenario;
        var previousShadowHandledAt = dealState.ActiveScenarioShadowHandledAtUtc;
        var previousWriterHandledAt = dealState.ActiveScenarioWriterHandledAtUtc;
        var previousLastAssignmentId = dealState.LastAssignmentId;
        var previousLastAppliedStageId = dealState.LastAppliedStageId;
        var previousLastAppliedResponsibleId = dealState.LastAppliedResponsibleId;

        MarkScenarioHandled(
            dealState,
            assignment.Scenario,
            assignment.OperationMode,
            assignment.DealAppliedAtUtc
            ?? assignment.AppliedAtUtc
            ?? (assignment.CreatedAtUtc == default
                ? handledAtUtc
                : assignment.CreatedAtUtc));
        dealState.LastAssignmentId = assignment.Id;
        if (assignment.DealAppliedAtUtc is not null
            || (assignment.OperationMode == BitrixWorkforceDistribution.ShadowMode
                && assignment.AppliedAtUtc is not null))
        {
            dealState.LastAppliedStageId = assignment.ToStageId;
            dealState.LastAppliedResponsibleId = assignment.SelectedResponsibleId;
        }

        return !string.Equals(previousScenario, dealState.ActiveScenario, StringComparison.Ordinal)
               || previousShadowHandledAt != dealState.ActiveScenarioShadowHandledAtUtc
               || previousWriterHandledAt != dealState.ActiveScenarioWriterHandledAtUtc
               || previousLastAssignmentId != dealState.LastAssignmentId
               || !string.Equals(
                   previousLastAppliedStageId,
                   dealState.LastAppliedStageId,
                   StringComparison.OrdinalIgnoreCase)
               || previousLastAppliedResponsibleId != dealState.LastAppliedResponsibleId;
    }

    private async Task PreserveExistingAssignmentGuardAsync(
        BitrixDealEventInboxEntity job,
        CancellationToken ct)
    {
        var assignment = await db.BitrixWorkforceAssignments
            .FirstOrDefaultAsync(x => x.InboxId == job.Id, ct);
        if (assignment is null
            || string.IsNullOrWhiteSpace(assignment.Scenario)
            || assignment.OperationMode is not (
                BitrixWorkforceDistribution.ShadowMode
                or BitrixWorkforceDistribution.WriterMode))
        {
            return;
        }

        var dealState = await db.BitrixWorkforceDealStates
            .FirstOrDefaultAsync(
                x => x.BitrixInstanceId == assignment.BitrixInstanceId
                     && x.DealId == assignment.DealId,
                ct);
        if (dealState is null
            || !string.Equals(
                dealState.ActiveScenario,
                assignment.Scenario,
                StringComparison.Ordinal))
        {
            return;
        }

        MarkScenarioHandled(
            dealState,
            assignment.Scenario,
            assignment.OperationMode,
            UtcNow());
        dealState.LastAssignmentId = assignment.Id;
    }

    private async Task<bool> HasCompetingWriterAsync(
        BitrixInstanceEntity instance,
        BitrixWorkforceConfigurationEntity configuration,
        string portalHost,
        CancellationToken ct)
    {
        var candidates = await db.BitrixWorkforceConfigurations
            .AsNoTracking()
            .Join(
                db.BitrixInstances.AsNoTracking(),
                workforce => workforce.BitrixInstanceId,
                portal => portal.Id,
                (workforce, portal) => new { Workforce = workforce, Portal = portal })
            .Where(x => x.Portal.Id != instance.Id
                        && x.Portal.IsEnabled
                        && x.Portal.DeletedAtUtc == null
                        && x.Workforce.OperationMode == BitrixWorkforceDistribution.WriterMode
                        && x.Workforce.DealCategoryId == configuration.DealCategoryId)
            .Select(x => x.Portal)
            .ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            var candidateWebhookUrl = await bitrixInstances.ResolveWebhookUrlAsync(candidate, ct);
            var candidateHost = BitrixWebhookValidator.TryGetPortalHost(candidateWebhookUrl)
                                ?? candidate.PortalHost?.Trim();
            if (string.Equals(candidateHost, portalHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
                     && x.Scenario == rule.Scenario
                     && x.OperationMode == configuration.OperationMode,
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
            OperationMode = configuration.OperationMode,
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
        var isNewAssignment = assignment is null;
        var wasAssigned = assignment?.Decision == BitrixWorkforceDecisions.Assigned;
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
            assignment.PreviousResponsibleId = deal.AssignedById;
        }

        if (decision == BitrixWorkforceDecisions.Assigned
            && !wasAssigned)
        {
            assignment.CreatedAtUtc = UtcNow();
        }

        assignment.Scenario = rule.Scenario;
        assignment.OperationMode = configuration.OperationMode;
        if (isNewAssignment || !wasAssigned)
        {
            assignment.FromStageId = deal.StageId;
            assignment.ToStageId = rule.TargetStageId;
        }

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
                var dealState = await db.BitrixWorkforceDealStates
                    .FirstOrDefaultAsync(
                        x => x.BitrixInstanceId == assignment.BitrixInstanceId
                             && x.DealId == assignment.DealId,
                        ct);
                if (dealState is not null
                    && string.Equals(
                        dealState.ActiveScenario,
                        assignment.Scenario,
                        StringComparison.Ordinal)
                    && assignment.OperationMode is (
                        BitrixWorkforceDistribution.ShadowMode
                        or BitrixWorkforceDistribution.WriterMode))
                {
                    MarkScenarioHandled(
                        dealState,
                        assignment.Scenario,
                        assignment.OperationMode,
                        UtcNow());
                    dealState.LastAssignmentId = assignment.Id;
                }
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

    private static bool ShouldPreserveExistingNewOwner(
        BitrixWorkforceDeal deal,
        IReadOnlyCollection<long> managerIds,
        long replaceableResponsibleId,
        string idempotencyFieldCode)
    {
        if (deal.AssignedById is long currentOwnerId
            && currentOwnerId > 0
            && (replaceableResponsibleId <= 0
                || currentOwnerId != replaceableResponsibleId))
        {
            return true;
        }

        var code = idempotencyFieldCode?.Trim() ?? string.Empty;
        var hasLeadFlowMarker = code.Length > 0
                                && deal.Fields.TryGetValue(code, out var marker)
                                && !string.IsNullOrWhiteSpace(marker);
        if (hasLeadFlowMarker)
        {
            return false;
        }

        return deal.CreatedById is long creatorId && managerIds.Contains(creatorId)
               || deal.ModifiedById is long modifierId && managerIds.Contains(modifierId);
    }

    private static string BuildReconciliationEventKey(
        Guid instanceId,
        long dealId,
        string stageId,
        string revision,
        DateOnly localDate,
        DateTime configurationRevisionAtUtc)
    {
        var canonical = string.Join(
            '|',
            "RECONCILE",
            instanceId.ToString("N"),
            dealId.ToString(CultureInfo.InvariantCulture),
            stageId.Trim().ToUpperInvariant(),
            configurationRevisionAtUtc.Ticks.ToString(CultureInfo.InvariantCulture),
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

    private async Task<IDbContextTransaction?> BeginSettingsWriteTransactionAsync(
        Guid bitrixInstanceId,
        CancellationToken ct)
    {
        if (!UsesPostgres())
        {
            return null;
        }

        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var lockKey = BuildAdvisoryLockKey($"settings|{bitrixInstanceId:D}");
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

    private async Task<BitrixWorkforceConfigurationEntity?> LockConfigurationForSelectionAsync(
        Guid bitrixInstanceId,
        CancellationToken ct)
    {
        if (!UsesPostgres())
        {
            return await db.BitrixWorkforceConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.BitrixInstanceId == bitrixInstanceId, ct);
        }

        var rows = await db.BitrixWorkforceConfigurations
            .FromSqlInterpolated(
                $"""
                 SELECT *
                 FROM "BitrixWorkforceConfigurations"
                 WHERE "BitrixInstanceId" = {bitrixInstanceId}
                 FOR UPDATE
                 """)
            .AsNoTracking()
            .ToListAsync(ct);
        return rows.SingleOrDefault();
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

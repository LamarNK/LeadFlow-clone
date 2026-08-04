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

public sealed class BitrixWorkforceProcessorTests
{
    [Fact]
    public async Task ShadowMode_UsesIndependentManagerRoundRobinWithoutWritingBitrix()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "PAUSED")
        ];
        harness.AddDeal(1001, "NEW");
        harness.AddDeal(1002, "NEW");
        await harness.SaveAsync();

        var processed = await harness.Sut.ProcessBatchAsync();

        Assert.Equal(2, processed);
        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.DealId)
            .ToListAsync();
        Assert.Equal([10L, 20L], assignments.Select(x => x.SelectedResponsibleId));
        Assert.All(assignments, x => Assert.Equal(BitrixWorkforceDecisions.Assigned, x.Decision));
        Assert.Empty(harness.Client.DealUpdates);
        Assert.All(
            harness.Db.BitrixDealEventInbox,
            x => Assert.Equal(BitrixWorkforceInboxStates.Completed, x.State));
    }

    [Fact]
    public async Task WriterMode_MovesSecondaryMissedCallAndSynchronizesContact()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_ENSD7E",
            "UC_FU2T4L",
            usesMorningWindow: true,
            writerConfirmed: true,
            reserveMinutes: 0,
            syncContactOwner: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.Client.ContactIds = [77];
        harness.AddDeal(
            2001,
            "UC_ENSD7E",
            comments:
            """
            Возраст: 34
            Вакансия: Сварщик
            Город: Омск
            """);
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var update = Assert.Single(harness.Client.DealUpdates);
        Assert.Equal(2001, update.DealId);
        Assert.Equal("UC_FU2T4L", update.Fields["STAGE_ID"]);
        Assert.Equal(10L, update.Fields["ASSIGNED_BY_ID"]);
        Assert.Equal("34", update.Fields["UF_AGE"]);
        Assert.Equal("Сварщик", update.Fields["UF_PROFESSION"]);
        Assert.Equal("Омск", update.Fields["UF_CITY"]);
        var contactUpdate = Assert.Single(harness.Client.ContactUpdates);
        Assert.Equal((77L, 10L), contactUpdate);

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.NotNull(assignment.AppliedAtUtc);
        Assert.Equal(10L, assignment.SelectedResponsibleId);
        Assert.Equal(BitrixWorkforceInboxStates.Completed, harness.Db.BitrixDealEventInbox.Single().State);
    }

    [Fact]
    public async Task WriterMode_DoesNotOverwritePopulatedAvitoFields()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(
            2101,
            "NEW",
            comments:
            """
            Возраст: 34
            Вакансия: Сварщик
            Город: Омск
            """);
        harness.Client.Deals[2101] = harness.Client.Deals[2101] with
        {
            Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["UF_AGE"] = "45",
                ["UF_PROFESSION"] = "Токарь",
                ["UF_CITY"] = "Тюмень"
            }
        };
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var update = Assert.Single(harness.Client.DealUpdates);
        Assert.DoesNotContain("UF_AGE", update.Fields.Keys);
        Assert.DoesNotContain("UF_PROFESSION", update.Fields.Keys);
        Assert.DoesNotContain("UF_CITY", update.Fields.Keys);
        Assert.Equal(10L, update.Fields["ASSIGNED_BY_ID"]);
    }

    [Fact]
    public async Task NoOpenManager_DefersJobWithoutMovingCursor()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "CLOSED"),
            new(20, false, null)
        ];
        harness.AddDeal(3001, "NEW");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var job = Assert.Single(harness.Db.BitrixDealEventInbox);
        Assert.Equal(BitrixWorkforceInboxStates.Pending, job.State);
        Assert.True(job.NextAttemptAtUtc > harness.Time.GetUtcNow().UtcDateTime);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
        Assert.Equal(
            BitrixWorkforceDecisions.Deferred,
            Assert.Single(harness.Db.BitrixWorkforceAssignments).Decision);
    }

    [Fact]
    public async Task DeferredOwnerChange_IsPreservedAndDoesNotAssignOnNextWebhook()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "CLOSED")];
        harness.AddDeal(3002, "NEW");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var waiting = Assert.Single(harness.Db.BitrixDealEventInbox);
        Assert.Equal(BitrixWorkforceInboxStates.Pending, waiting.State);
        Assert.Equal(
            BitrixWorkforceDecisions.Deferred,
            Assert.Single(harness.Db.BitrixWorkforceAssignments).Decision);

        harness.Client.Deals[3002] = harness.Client.Deals[3002] with
        {
            AssignedById = 999
        };
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        await harness.Sut.ProcessBatchAsync();

        harness.AddEvent(3002, "owner-changed-after-defer-3002");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.All(assignments, x => Assert.Equal(BitrixWorkforceDecisions.Ignored, x.Decision));
        Assert.Contains("responsible changed", assignments[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already handled", assignments[1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Equal(999L, harness.Client.Deals[3002].AssignedById);
    }

    [Fact]
    public async Task NormalDeferrals_DoNotConsumeFailureRetryBudget()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true,
            maxAttempts: 2);
        harness.Client.ManagerStatuses = [new(10, true, "CLOSED")];
        harness.AddDeal(4001, "NEW");
        await harness.SaveAsync();

        for (var index = 0; index < 3; index++)
        {
            await harness.Sut.ProcessBatchAsync();
            harness.Time.Now = harness.Time.Now.AddMinutes(1);
        }

        var waitingJob = Assert.Single(harness.Db.BitrixDealEventInbox);
        Assert.Equal(3, waitingJob.AttemptCount);
        Assert.Equal(0, waitingJob.FailureCount);

        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.Client.DealUpdateFailuresRemaining = 1;
        await harness.Sut.ProcessBatchAsync();

        var failedOnceJob = Assert.Single(harness.Db.BitrixDealEventInbox);
        Assert.Equal(1, failedOnceJob.FailureCount);
        Assert.Equal(BitrixWorkforceInboxStates.Pending, failedOnceJob.State);
    }

    [Fact]
    public async Task Retry_KeepsSelectedManagerAndDoesNotAdvanceCursorTwice()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.Client.DealUpdateFailuresRemaining = 1;
        harness.AddDeal(5001, "NEW");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();
        var firstAssignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(10L, firstAssignment.SelectedResponsibleId);
        Assert.Null(firstAssignment.AppliedAtUtc);

        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        harness.Client.ManagerStatuses = [new(20, true, "OPENED")];
        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(10L, assignment.SelectedResponsibleId);
        Assert.NotNull(assignment.AppliedAtUtc);
        var cursor = Assert.Single(harness.Db.BitrixWorkforceCursors);
        Assert.Equal(10L, cursor.LastAssignedBitrixUserId);
        Assert.Equal(2, harness.Client.DealUpdateAttempts);
        Assert.Single(harness.Client.DealUpdates);
    }

    [Fact]
    public async Task Retry_AfterDealWasAppliedButResponseWasLost_KeepsFirstManager()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.Client.DealUpdateResponseFailuresRemaining = 1;
        harness.AddDeal(5002, "NEW");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var failedOnce = Assert.Single(harness.Db.BitrixDealEventInbox);
        Assert.Equal(BitrixWorkforceInboxStates.Pending, failedOnce.State);
        Assert.Equal(10L, harness.Client.Deals[5002].AssignedById);

        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        harness.Client.ManagerStatuses = [new(20, true, "OPENED")];
        await harness.Sut.ProcessBatchAsync();

        harness.AddEvent(5002, "response-lost-follow-up-5002");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(10L, assignments[0].SelectedResponsibleId);
        Assert.NotNull(assignments[0].AppliedAtUtc);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Equal(10L, harness.Client.Deals[5002].AssignedById);
        Assert.Equal(1, harness.Client.DealUpdateAttempts);
        Assert.Single(harness.Client.DealUpdates);
        var cursor = Assert.Single(harness.Db.BitrixWorkforceCursors);
        Assert.Equal(BitrixWorkforceDistribution.WriterMode, cursor.OperationMode);
        Assert.Equal(10L, cursor.LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task Retry_DoesNotApplyStaleAssignmentAfterDealChangedScenario()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.AddRule(
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_ENSD7E",
            "UC_FU2T4L",
            usesMorningWindow: false,
            sortOrder: 1);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.Client.DealUpdateFailuresRemaining = 1;
        harness.AddDeal(6001, "NEW");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();
        harness.Client.Deals[6001] = harness.Client.Deals[6001] with
        {
            StageId = "UC_ENSD7E"
        };
        harness.Time.Now = harness.Time.Now.AddMinutes(1);

        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignment.Decision);
        Assert.Equal(BitrixWorkforceDistribution.NewScenario, assignment.Scenario);
        Assert.Contains("current state is preserved", assignment.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Equal("UC_ENSD7E", harness.Client.Deals[6001].StageId);
        Assert.Null(harness.Client.Deals[6001].AssignedById);
        var cursor = Assert.Single(harness.Db.BitrixWorkforceCursors);
        Assert.Equal(BitrixWorkforceDistribution.NewScenario, cursor.Scenario);
        Assert.Equal(10L, cursor.LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task MorningReserve_ReleasesToDifferentManagerWhoStartsLater()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.SubstituteMissedCallScenario,
            "UC_WT8KQY",
            "UC_6OQRTF",
            usesMorningWindow: true,
            reserveMinutes: 120,
            releasePercent: 50);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(7001, "UC_WT8KQY");
        harness.AddDeal(7002, "UC_WT8KQY");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var firstPass = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.DealId)
            .ToListAsync();
        Assert.Equal(BitrixWorkforceDecisions.Assigned, firstPass[0].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Reserved, firstPass[1].Decision);

        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        harness.Client.ManagerStatuses = [new(20, true, "OPENED")];
        await harness.Sut.ProcessBatchAsync();

        var released = await harness.Db.BitrixWorkforceAssignments
            .SingleAsync(x => x.DealId == 7002);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, released.Decision);
        Assert.Equal(20L, released.SelectedResponsibleId);
        Assert.NotNull(released.AppliedAtUtc);
    }

    [Fact]
    public async Task ContactFailure_RetryResumesSagaWithoutUpdatingDealTwice()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_ENSD7E",
            "UC_FU2T4L",
            usesMorningWindow: false,
            writerConfirmed: true,
            syncContactOwner: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.Client.ContactIds = [77];
        harness.Client.ContactUpdateFailuresRemaining = 1;
        harness.AddDeal(8001, "UC_ENSD7E");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();
        var partial = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.NotNull(partial.DealAppliedAtUtc);
        Assert.Null(partial.ContactsAppliedAtUtc);
        Assert.Null(partial.AppliedAtUtc);

        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        await harness.Sut.ProcessBatchAsync();

        var completed = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.NotNull(completed.ContactsAppliedAtUtc);
        Assert.NotNull(completed.AppliedAtUtc);
        Assert.Equal(1, harness.Client.DealUpdateAttempts);
        Assert.Single(harness.Client.DealUpdates);
        Assert.Single(harness.Client.ContactUpdates);
    }

    [Fact]
    public async Task ContactSync_UpdatesOnlyContactWithOriginalDealOwner()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_CONTACT_SOURCE",
            "UC_CONTACT_TARGET",
            usesMorningWindow: false,
            writerConfirmed: true,
            syncContactOwner: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.Client.ContactIds = [71, 72, 73];
        harness.Client.ContactOwners[71] = 777;
        harness.Client.ContactOwners[72] = 999;
        harness.Client.ContactOwners[73] = 10;
        harness.AddDeal(8002, "UC_CONTACT_SOURCE");
        harness.Client.Deals[8002] = harness.Client.Deals[8002] with
        {
            AssignedById = 777
        };
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignment.Decision);
        Assert.Equal(777L, assignment.PreviousResponsibleId);
        Assert.Equal(10L, assignment.SelectedResponsibleId);
        Assert.NotNull(assignment.AppliedAtUtc);
        Assert.Equal((71L, 10L), Assert.Single(harness.Client.ContactUpdates));
        Assert.Equal(10L, harness.Client.ContactOwners[71]);
        Assert.Equal(999L, harness.Client.ContactOwners[72]);
        Assert.Equal(10L, harness.Client.ContactOwners[73]);
        Assert.Equal(10L, harness.Client.Deals[8002].AssignedById);
    }

    [Fact]
    public async Task DuplicateEvent_AfterDealStep_IsIgnoredWhileContactRetryCompletes()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_ENSD7E",
            "UC_FU2T4L",
            usesMorningWindow: false,
            writerConfirmed: true,
            syncContactOwner: true);
        harness.AddRule(
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_FU2T4L",
            "UC_FU2T4L",
            usesMorningWindow: false,
            sortOrder: 1);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.Client.ContactIds = [77];
        harness.Client.ContactUpdateFailuresRemaining = 1;
        harness.AddDeal(9001, "UC_ENSD7E");
        harness.AddEvent(9001, "duplicate-event-9001");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var firstPass = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, firstPass.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, firstPass[0].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, firstPass[1].Decision);
        Assert.Contains("already matches", firstPass[1].Reason, StringComparison.OrdinalIgnoreCase);

        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        await harness.Sut.ProcessBatchAsync();

        var completed = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(BitrixWorkforceDecisions.Assigned, completed[0].Decision);
        Assert.NotNull(completed[0].AppliedAtUtc);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, completed[1].Decision);
        Assert.Contains("already matches", completed[1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(harness.Db.BitrixWorkforceCursors);
        Assert.Single(harness.Client.DealUpdates);
    }

    [Fact]
    public async Task Reconcile_DeduplicatesRevisionAndEnqueuesDealAgainAfterChange()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.SetDeal(10001, "NEW");
        harness.Client.Deals[10001] = harness.Client.Deals[10001] with
        {
            Fields = new Dictionary<string, string?>
            {
                ["DATE_MODIFY"] = "2026-07-30T09:00:00+03:00"
            }
        };
        await harness.SaveAsync();

        Assert.Equal(1, await harness.Sut.ReconcileAsync());
        Assert.Equal(0, await harness.Sut.ReconcileAsync());
        await harness.Sut.ProcessBatchAsync();
        Assert.Equal(0, await harness.Sut.ReconcileAsync());

        harness.Client.Deals[10001] = harness.Client.Deals[10001] with
        {
            Fields = new Dictionary<string, string?>
            {
                ["DATE_MODIFY"] = "2026-07-30T10:00:00+03:00"
            }
        };

        Assert.Equal(1, await harness.Sut.ReconcileAsync());
        Assert.Equal(2, await harness.Db.BitrixDealEventInbox.CountAsync());
    }

    [Fact]
    public async Task Reconcile_ConfigurationRevisionChange_EnqueuesSameDealRevisionAgain()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.SetDeal(10002, "NEW");
        harness.Client.Deals[10002] = harness.Client.Deals[10002] with
        {
            Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["DATE_MODIFY"] = "2026-07-30T09:00:00+03:00"
            }
        };
        await harness.SaveAsync();

        Assert.Equal(1, await harness.Sut.ReconcileAsync());
        await harness.Sut.ProcessBatchAsync();
        Assert.Equal(0, await harness.Sut.ReconcileAsync());

        var configuration = Assert.Single(harness.Db.BitrixWorkforceConfigurations);
        configuration.UpdatedAtUtc = configuration.UpdatedAtUtc.AddMinutes(1);
        await harness.SaveAsync();

        Assert.Equal(1, await harness.Sut.ReconcileAsync());
        var inbox = await harness.Db.BitrixDealEventInbox
            .OrderBy(x => x.Id)
            .ToListAsync();
        Assert.Equal(2, inbox.Count);
        Assert.All(inbox, x => Assert.Equal("RECONCILE", x.EventName));
        Assert.All(inbox, x => Assert.Equal(10002, x.DealId));
        Assert.Equal(2, inbox.Select(x => x.EventKey).Distinct().Count());
        Assert.Equal(
            "2026-07-30T09:00:00+03:00",
            harness.Client.Deals[10002].Fields["DATE_MODIFY"]);
        Assert.Equal("NEW", harness.Client.Deals[10002].StageId);
    }

    [Fact]
    public async Task ShadowMode_HandledScenarioAfterConfigurationRevision_RefreshesCoverageMarkerWithoutWrite()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            integrationResponsibleId: 999);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(10003, "NEW");
        harness.Client.Deals[10003] = harness.Client.Deals[10003] with
        {
            AssignedById = 999,
            Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["DATE_MODIFY"] = "2026-07-30T09:00:00+03:00"
            }
        };
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var initialState = Assert.Single(harness.Db.BitrixWorkforceDealStates);
        var initialHandledAt = Assert.IsType<DateTime>(
            initialState.ActiveScenarioShadowHandledAtUtc);
        var initialCursorAt = Assert.IsType<DateTime>(
            Assert.Single(harness.Db.BitrixWorkforceCursors).LastAssignedAtUtc);
        Assert.Equal(999L, harness.Client.Deals[10003].AssignedById);
        Assert.Empty(harness.Client.DealUpdates);

        harness.Time.Now = harness.Time.Now.AddMinutes(5);
        var configuration = Assert.Single(harness.Db.BitrixWorkforceConfigurations);
        configuration.UpdatedAtUtc = harness.Time.GetUtcNow().UtcDateTime;
        var configurationRevision = configuration.UpdatedAtUtc;
        await harness.SaveAsync();

        Assert.Equal(1, await harness.Sut.ReconcileAsync());
        await harness.Sut.ProcessBatchAsync();

        var refreshedState = Assert.Single(harness.Db.BitrixWorkforceDealStates);
        var refreshedHandledAt = Assert.IsType<DateTime>(
            refreshedState.ActiveScenarioShadowHandledAtUtc);
        Assert.True(refreshedHandledAt > initialHandledAt);
        Assert.Equal(harness.Time.GetUtcNow().UtcDateTime, refreshedHandledAt);
        Assert.True(refreshedHandledAt >= configurationRevision);
        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Equal(initialCursorAt, Assert.Single(harness.Db.BitrixWorkforceCursors)
            .LastAssignedAtUtc);
        Assert.Equal(999L, harness.Client.Deals[10003].AssignedById);
        Assert.Equal(1, harness.Client.ManagerStatusAttempts);
        Assert.Empty(harness.Client.DealUpdates);
    }

    [Fact]
    public async Task ShadowMode_HandledStageRemappedToNewScenario_RefreshesGuardWithoutRedistribution()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            "scenario_a",
            "S",
            "S",
            usesMorningWindow: false);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(10004, "S");
        harness.Client.Deals[10004] = harness.Client.Deals[10004] with
        {
            AssignedById = 999,
            Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["DATE_MODIFY"] = "2026-07-30T09:00:00+03:00"
            }
        };
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var originalState = Assert.Single(harness.Db.BitrixWorkforceDealStates);
        Assert.Equal("scenario_a", originalState.ActiveScenario);
        var originalHandledAt = Assert.IsType<DateTime>(
            originalState.ActiveScenarioShadowHandledAtUtc);
        Assert.Equal("scenario_a", Assert.Single(harness.Db.BitrixWorkforceCursors).Scenario);

        harness.Time.Now = harness.Time.Now.AddMinutes(5);
        var configuration = Assert.Single(harness.Db.BitrixWorkforceConfigurations);
        configuration.UpdatedAtUtc = harness.Time.GetUtcNow().UtcDateTime;
        var configurationRevision = configuration.UpdatedAtUtc;
        var rule = Assert.Single(harness.Db.BitrixWorkforceStageRules);
        rule.Scenario = "scenario_b";
        await harness.SaveAsync();

        Assert.Equal(1, await harness.Sut.ReconcileAsync());
        await harness.Sut.ProcessBatchAsync();

        var remappedState = Assert.Single(harness.Db.BitrixWorkforceDealStates);
        Assert.Equal("scenario_b", remappedState.ActiveScenario);
        var remappedHandledAt = Assert.IsType<DateTime>(
            remappedState.ActiveScenarioShadowHandledAtUtc);
        Assert.True(remappedHandledAt > originalHandledAt);
        Assert.True(remappedHandledAt >= configurationRevision);
        Assert.Equal(999L, harness.Client.Deals[10004].AssignedById);
        Assert.Empty(harness.Client.DealUpdates);
        var cursor = Assert.Single(harness.Db.BitrixWorkforceCursors);
        Assert.Equal("scenario_a", cursor.Scenario);
        Assert.Equal(10L, cursor.LastAssignedBitrixUserId);

        harness.AddEvent(10004, "after-shadow-scenario-remap-10004");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(3, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.All(assignments.Skip(1), x =>
            Assert.Equal(BitrixWorkforceDecisions.Ignored, x.Decision));
        Assert.Equal("scenario_b", Assert.Single(harness.Db.BitrixWorkforceDealStates)
            .ActiveScenario);
        Assert.Equal(1, harness.Client.ManagerStatusAttempts);
        Assert.Equal(999L, harness.Client.Deals[10004].AssignedById);
        Assert.Single(harness.Db.BitrixWorkforceCursors);
        Assert.Empty(harness.Client.DealUpdates);
    }

    [Fact]
    public async Task ShadowMode_RepeatedUpdateInSameStage_DoesNotAdvanceCursorAgain()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(11001, "NEW");
        harness.AddEvent(11001, "second-update-11001");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(10L, assignments[0].SelectedResponsibleId);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        var cursor = Assert.Single(harness.Db.BitrixWorkforceCursors);
        Assert.Equal(10L, cursor.LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task ShadowMode_DealLeavesAndReturnsToStage_AdvancesCursorAgain()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(12001, "NEW");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        harness.SetDeal(12001, "WON");
        harness.AddEvent(12001, "left-new-12001");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        harness.SetDeal(12001, "NEW");
        harness.AddEvent(12001, "returned-new-12001");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(10L, assignments[0].SelectedResponsibleId);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[2].Decision);
        Assert.Equal(20L, assignments[2].SelectedResponsibleId);
        var cursor = Assert.Single(harness.Db.BitrixWorkforceCursors);
        Assert.Equal(20L, cursor.LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task WriterMode_OwnerChangeInsideHandledScenario_DoesNotReassign()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(12501, "NEW");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        harness.Client.Deals[12501] = harness.Client.Deals[12501] with
        {
            AssignedById = 999
        };
        harness.AddEvent(12501, "owner-changed-12501");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(10L, assignments[0].SelectedResponsibleId);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Contains("already handled", assignments[1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(harness.Client.DealUpdates);
        Assert.Equal(10L, Assert.Single(harness.Db.BitrixWorkforceCursors).LastAssignedBitrixUserId);
        Assert.Equal(999L, harness.Client.Deals[12501].AssignedById);
    }

    [Fact]
    public async Task WriterMode_ReconciledOwnerChangeInsideHandledScenario_DoesNotReassign()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(12507, "NEW");
        harness.Client.Deals[12507] = harness.Client.Deals[12507] with
        {
            Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["DATE_MODIFY"] = "2026-08-04T09:00:00+03:00"
            }
        };
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        harness.Client.Deals[12507] = harness.Client.Deals[12507] with
        {
            AssignedById = 999,
            Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["DATE_MODIFY"] = "2026-08-04T10:00:00+03:00"
            }
        };
        Assert.Equal(1, await harness.Sut.ReconcileAsync());
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Contains("already handled", assignments[1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(harness.Client.DealUpdates);
        Assert.Equal(10L, Assert.Single(harness.Db.BitrixWorkforceCursors).LastAssignedBitrixUserId);
        Assert.Equal(999L, harness.Client.Deals[12507].AssignedById);
    }

    [Fact]
    public async Task WriterMode_TargetStageInSameScenario_DoesNotReassignAfterOwnerChange()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_ENSD7E",
            "UC_FU2T4L",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.AddRule(
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_FU2T4L",
            "UC_FU2T4L",
            usesMorningWindow: false,
            sortOrder: 1);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(12502, "UC_ENSD7E");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();
        Assert.Equal("UC_FU2T4L", harness.Client.Deals[12502].StageId);

        harness.Client.Deals[12502] = harness.Client.Deals[12502] with
        {
            AssignedById = 999
        };
        harness.AddEvent(12502, "target-owner-changed-12502");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Single(harness.Client.DealUpdates);
        Assert.Equal(10L, Assert.Single(harness.Db.BitrixWorkforceCursors).LastAssignedBitrixUserId);
        Assert.Equal(999L, harness.Client.Deals[12502].AssignedById);
    }

    [Fact]
    public async Task WriterMode_CrossScenarioTargetSource_DoesNotCascadeIntoSecondScenario()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_SOURCE_A",
            "UC_SHARED",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.AddRule(
            BitrixWorkforceDistribution.SubstituteMissedCallScenario,
            "UC_SHARED",
            "UC_SHARED",
            usesMorningWindow: false,
            sortOrder: 1);
        harness.AddCursor(
            BitrixWorkforceDistribution.SubstituteMissedCallScenario,
            BitrixWorkforceDistribution.WriterMode,
            lastManagerId: 10);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(12509, "UC_SOURCE_A");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();
        Assert.Equal("UC_SHARED", harness.Client.Deals[12509].StageId);
        Assert.Equal(10L, harness.Client.Deals[12509].AssignedById);

        harness.AddEvent(12509, "cross-scenario-target-update-12509");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(BitrixWorkforceDistribution.MissedCallScenario, assignments[0].Scenario);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Equal("stage_not_configured", assignments[1].Scenario);
        Assert.Equal(10L, harness.Client.Deals[12509].AssignedById);
        Assert.Single(harness.Client.DealUpdates);

        var substituteCursor = await harness.Db.BitrixWorkforceCursors.SingleAsync(x =>
            x.Scenario == BitrixWorkforceDistribution.SubstituteMissedCallScenario
            && x.OperationMode == BitrixWorkforceDistribution.WriterMode);
        Assert.Equal(10L, substituteCursor.LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task WriterMode_ManagerMovesBetweenNdzStagesInSameScenario_PreservesOwner()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_FU2T4L",
            "UC_FU2T4L",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.AddRule(
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_ENSD7E",
            "UC_FU2T4L",
            usesMorningWindow: false,
            sortOrder: 1);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(12508, "UC_FU2T4L");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();
        Assert.Equal(10L, harness.Client.Deals[12508].AssignedById);

        harness.Client.Deals[12508] = harness.Client.Deals[12508] with
        {
            StageId = "UC_ENSD7E"
        };
        harness.AddEvent(12508, "manager-moved-ndz-stage-12508");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Contains("already handled", assignments[1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(harness.Client.DealUpdates);
        Assert.Equal("UC_ENSD7E", harness.Client.Deals[12508].StageId);
        Assert.Equal(10L, harness.Client.Deals[12508].AssignedById);
        Assert.Equal(10L, Assert.Single(harness.Db.BitrixWorkforceCursors).LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task WriterMode_DealLeavesAndReturnsToScenario_AssignsOncePerEntry()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(12503, "NEW");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        harness.Client.Deals[12503] = harness.Client.Deals[12503] with
        {
            StageId = "WON"
        };
        harness.AddEvent(12503, "left-scenario-12503");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        harness.Client.Deals[12503] = harness.Client.Deals[12503] with
        {
            StageId = "NEW"
        };
        harness.AddEvent(12503, "returned-scenario-12503");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        harness.Client.Deals[12503] = harness.Client.Deals[12503] with
        {
            AssignedById = 999
        };
        harness.AddEvent(12503, "repeat-after-return-12503");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(4, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(10L, assignments[0].SelectedResponsibleId);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Equal("stage_not_configured", assignments[1].Scenario);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[2].Decision);
        Assert.Equal(20L, assignments[2].SelectedResponsibleId);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[3].Decision);
        Assert.Equal(2, harness.Client.DealUpdates.Count);
        Assert.Equal(20L, Assert.Single(harness.Db.BitrixWorkforceCursors).LastAssignedBitrixUserId);
        Assert.Equal(999L, harness.Client.Deals[12503].AssignedById);
    }

    [Fact]
    public async Task ShadowMode_SourceToTargetScenario_IsHandledOnlyOnce()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_ENSD7E",
            "UC_FU2T4L",
            usesMorningWindow: false);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(12504, "UC_ENSD7E");
        harness.AddEvent(12504, "repeat-shadow-source-12504");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(10L, assignments[0].SelectedResponsibleId);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Equal(10L, Assert.Single(harness.Db.BitrixWorkforceCursors).LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task ShadowDecision_BlocksWriterForSameEntry_AndCursorsAreModeIndependent()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(12505, "NEW");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var configuration = await harness.Db.BitrixWorkforceConfigurations.SingleAsync();
        configuration.OperationMode = BitrixWorkforceDistribution.WriterMode;
        configuration.WriterRulesConfirmed = true;
        configuration.UpdatedAtUtc = configuration.UpdatedAtUtc.AddMinutes(1);
        harness.AddEvent(12505, "writer-after-shadow-12505");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDistribution.ShadowMode, assignments[0].OperationMode);
        Assert.Equal(10L, assignments[0].SelectedResponsibleId);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Contains("already handled", assignments[1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Null(harness.Client.Deals[12505].AssignedById);

        harness.AddDeal(12510, "NEW");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        Assert.Equal(10L, harness.Client.Deals[12510].AssignedById);
        Assert.Single(harness.Client.DealUpdates);
        var cursors = await harness.Db.BitrixWorkforceCursors
            .OrderBy(x => x.OperationMode)
            .ToListAsync();
        Assert.Equal(2, cursors.Count);
        Assert.Contains(
            cursors,
            x => x.OperationMode == BitrixWorkforceDistribution.ShadowMode
                 && x.LastAssignedBitrixUserId == 10);
        Assert.Contains(
            cursors,
            x => x.OperationMode == BitrixWorkforceDistribution.WriterMode
                 && x.LastAssignedBitrixUserId == 10);
    }

    [Fact]
    public async Task WriterMode_FirstLeadFlowDealWithNonDefaultResponsible_PreservesOwner()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true,
            integrationResponsibleId: 777);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(12506, "NEW");
        harness.Client.Deals[12506] = harness.Client.Deals[12506] with
        {
            AssignedById = 999,
            Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["UF_IDEMPOTENCY"] = "leadflow:12506"
            }
        };
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignment.Decision);
        Assert.Contains("current owner is preserved", assignment.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(999L, harness.Client.Deals[12506].AssignedById);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
    }

    [Fact]
    public async Task WriterMode_FirstLeadFlowDealWithConfiguredResponsible_IsDistributed()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true,
            integrationResponsibleId: 999);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(12506, "NEW");
        harness.Client.Deals[12506] = harness.Client.Deals[12506] with
        {
            AssignedById = 999,
            Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["UF_IDEMPOTENCY"] = "leadflow:12506"
            }
        };
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignment.Decision);
        Assert.Equal(10L, assignment.SelectedResponsibleId);
        Assert.Equal(10L, harness.Client.Deals[12506].AssignedById);
        Assert.Single(harness.Client.DealUpdates);
    }

    [Fact]
    public async Task WriterMode_RemoteChangeBeforeWrite_CancelsAssignment()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(13001, "NEW");
        var original = harness.Client.Deals[13001];
        harness.Client.SetDealReadSequence(
            13001,
            original,
            original with
            {
                StageId = "WON",
                AssignedById = 999
            });
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignment.Decision);
        Assert.Null(assignment.AppliedAtUtc);
        Assert.Equal(2, harness.Client.GetDealAttempts);
        Assert.Equal(0, harness.Client.DealUpdateAttempts);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Equal(
            BitrixWorkforceInboxStates.Completed,
            Assert.Single(harness.Db.BitrixDealEventInbox).State);
    }

    [Fact]
    public async Task WriterMode_OwnerChangeBeforeWrite_DoesNotReassignOnNextWebhook()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(13002, "NEW");
        var original = harness.Client.Deals[13002];
        var ownerChanged = original with { AssignedById = 999 };
        harness.Client.Deals[13002] = ownerChanged;
        harness.Client.SetDealReadSequence(13002, original, ownerChanged);
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        harness.AddEvent(13002, "owner-changed-before-write-follow-up-13002");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.All(assignments, x => Assert.Equal(BitrixWorkforceDecisions.Ignored, x.Decision));
        Assert.Contains("changed before", assignments[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already handled", assignments[1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Equal(999L, harness.Client.Deals[13002].AssignedById);
        var cursor = Assert.Single(harness.Db.BitrixWorkforceCursors);
        Assert.Equal(BitrixWorkforceDistribution.WriterMode, cursor.OperationMode);
        Assert.Equal(10L, cursor.LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task NewCreatedManuallyByManager_PreservesOwnerAndDoesNotMoveCursor()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(13501, "NEW");
        harness.Client.Deals[13501] = harness.Client.Deals[13501] with
        {
            CreatedById = 10,
            AssignedById = 10
        };
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignment.Decision);
        Assert.Contains("manually", assignment.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
        Assert.Empty(harness.Client.DealUpdates);
    }

    [Fact]
    public async Task NewCreatedByManagerWithLeadFlowMarker_IsDistributed()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            integrationResponsibleId: 10);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(13502, "NEW");
        harness.Client.Deals[13502] = harness.Client.Deals[13502] with
        {
            CreatedById = 10,
            AssignedById = 10,
            Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["UF_IDEMPOTENCY"] = "leadflow:13502"
            }
        };
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignment.Decision);
        Assert.Equal(10L, assignment.SelectedResponsibleId);
        Assert.Equal(10L, Assert.Single(harness.Db.BitrixWorkforceCursors).LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task WriterMode_ConfigurationChangeDuringSelection_DoesNotMoveCursorOrReassign()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(14001, "NEW");
        harness.Client.BeforeManagerStatusesReturnAsync = () =>
            harness.MutateConfigurationOutOfBandAsync(configuration =>
                configuration.UpdatedAtUtc = configuration.UpdatedAtUtc.AddMinutes(1));
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignment.Decision);
        Assert.Null(assignment.AppliedAtUtc);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Equal(
            BitrixWorkforceInboxStates.Completed,
            Assert.Single(harness.Db.BitrixDealEventInbox).State);

        harness.Client.Deals[14001] = harness.Client.Deals[14001] with
        {
            AssignedById = 999
        };
        harness.AddEvent(14001, "after-selection-config-change-14001");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.All(assignments, x => Assert.Equal(BitrixWorkforceDecisions.Ignored, x.Decision));
        Assert.Contains("already handled", assignments[1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, harness.Client.ManagerStatusAttempts);
        Assert.Equal(999L, harness.Client.Deals[14001].AssignedById);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
        Assert.Empty(harness.Client.DealUpdates);
    }

    [Fact]
    public async Task WriterMode_DisabledWhileRefreshingDeal_DoesNotWrite()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(14501, "NEW");
        harness.Client.BeforeDealReadReturnAsync = attempt =>
            attempt == 2
                ? harness.MutateConfigurationOutOfBandAsync(configuration =>
                {
                    configuration.OperationMode = BitrixWorkforceDistribution.DisabledMode;
                    configuration.WriterRulesConfirmed = false;
                    configuration.UpdatedAtUtc = configuration.UpdatedAtUtc.AddMinutes(1);
                })
                : Task.CompletedTask;
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignment.Decision);
        Assert.Null(assignment.AppliedAtUtc);
        Assert.Equal(2, harness.Client.GetDealAttempts);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Equal(
            BitrixWorkforceInboxStates.Completed,
            Assert.Single(harness.Db.BitrixDealEventInbox).State);
    }

    [Fact]
    public async Task WriterMode_DisabledAfterDealUpdate_DoesNotSynchronizeContacts()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_ENSD7E",
            "UC_FU2T4L",
            usesMorningWindow: false,
            writerConfirmed: true,
            syncContactOwner: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.Client.ContactIds = [77];
        harness.AddDeal(15001, "UC_ENSD7E");
        harness.Client.AfterDealUpdateAsync = () =>
            harness.MutateConfigurationOutOfBandAsync(configuration =>
            {
                configuration.OperationMode = BitrixWorkforceDistribution.DisabledMode;
                configuration.WriterRulesConfirmed = false;
                configuration.UpdatedAtUtc = configuration.UpdatedAtUtc.AddMinutes(1);
            });
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.NotNull(assignment.DealAppliedAtUtc);
        Assert.Null(assignment.ContactsAppliedAtUtc);
        Assert.Null(assignment.AppliedAtUtc);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignment.Decision);
        Assert.Single(harness.Client.DealUpdates);
        Assert.Empty(harness.Client.ContactUpdates);
        Assert.Equal(
            BitrixWorkforceInboxStates.Completed,
            Assert.Single(harness.Db.BitrixDealEventInbox).State);
    }

    [Fact]
    public async Task WriterMode_InstanceDisabledWhileRefreshingDeal_DoesNotWrite()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(14502, "NEW");
        harness.Client.BeforeDealReadReturnAsync = attempt =>
            attempt == 2
                ? harness.MutateInstanceOutOfBandAsync(instance =>
                {
                    instance.IsEnabled = false;
                    instance.UpdatedAtUtc = instance.UpdatedAtUtc.AddMinutes(1);
                })
                : Task.CompletedTask;
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignment.Decision);
        Assert.Null(assignment.AppliedAtUtc);
        Assert.Equal(2, harness.Client.GetDealAttempts);
        Assert.Equal(0, harness.Client.DealUpdateAttempts);
        Assert.Null(harness.Client.Deals[14502].AssignedById);
        Assert.False(
            (await harness.Db.BitrixInstances
                .AsNoTracking()
                .SingleAsync(x => x.Id == harness.InstanceId))
            .IsEnabled);
        Assert.Equal(
            BitrixWorkforceInboxStates.Completed,
            Assert.Single(harness.Db.BitrixDealEventInbox).State);
    }

    [Fact]
    public async Task LegacyPartialAssignmentWithoutLastAssignmentId_HydratesGuardAndPreservesOwner()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(16001, "NEW");
        harness.Client.Deals[16001] = harness.Client.Deals[16001] with
        {
            AssignedById = 10
        };
        await harness.SaveAsync();

        var inbox = Assert.Single(harness.Db.BitrixDealEventInbox);
        var configuration = Assert.Single(harness.Db.BitrixWorkforceConfigurations);
        var assignmentId = Guid.NewGuid();
        harness.Db.BitrixWorkforceDealStates.Add(new BitrixWorkforceDealStateEntity
        {
            BitrixInstanceId = harness.InstanceId,
            DealId = 16001,
            LastObservedStageId = "NEW",
            LastAppliedStageId = "NEW",
            LastAppliedResponsibleId = 10,
            LastAssignmentId = null,
            UpdatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
        });
        harness.Db.BitrixWorkforceAssignments.Add(new BitrixWorkforceAssignmentEntity
        {
            Id = assignmentId,
            InboxId = inbox.Id,
            BitrixInstanceId = harness.InstanceId,
            DealId = 16001,
            Scenario = BitrixWorkforceDistribution.NewScenario,
            OperationMode = BitrixWorkforceDistribution.WriterMode,
            FromStageId = "NEW",
            ToStageId = "NEW",
            PreviousResponsibleId = null,
            SelectedResponsibleId = 10,
            Decision = BitrixWorkforceDecisions.Assigned,
            Reason = "Legacy partial writer assignment.",
            CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime.AddMinutes(-1),
            ConfigurationRevisionAtUtc = configuration.UpdatedAtUtc,
            DealAppliedAtUtc = harness.Time.GetUtcNow().UtcDateTime.AddSeconds(-30)
        });
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var hydratedState = Assert.Single(harness.Db.BitrixWorkforceDealStates);
        Assert.Equal(assignmentId, hydratedState.LastAssignmentId);
        Assert.NotNull(hydratedState.ActiveScenarioWriterHandledAtUtc);

        harness.Client.Deals[16001] = harness.Client.Deals[16001] with
        {
            AssignedById = 999
        };
        harness.AddEvent(16001, "legacy-partial-owner-change-16001");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Equal(999L, harness.Client.Deals[16001].AssignedById);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
    }

    [Fact]
    public async Task WriterMode_ScenarioAToBToA_AssignsExactlyOncePerEntry()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "STAGE_A",
            "STAGE_A",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.AddRule(
            BitrixWorkforceDistribution.MissedCallScenario,
            "STAGE_B",
            "STAGE_B",
            usesMorningWindow: false,
            sortOrder: 1);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(16002, "STAGE_A");
        harness.AddEvent(16002, "stage-a-duplicate-16002");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        harness.Client.Deals[16002] = harness.Client.Deals[16002] with
        {
            StageId = "STAGE_B"
        };
        harness.AddEvent(16002, "entered-stage-b-16002");
        harness.AddEvent(16002, "stage-b-duplicate-16002");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        harness.Client.Deals[16002] = harness.Client.Deals[16002] with
        {
            StageId = "STAGE_A"
        };
        harness.AddEvent(16002, "returned-stage-a-16002");
        harness.AddEvent(16002, "returned-stage-a-duplicate-16002");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(6, assignments.Count);
        var applied = assignments
            .Where(x => x.Decision == BitrixWorkforceDecisions.Assigned)
            .ToList();
        Assert.Equal(3, applied.Count);
        Assert.Equal(
            [
                BitrixWorkforceDistribution.NewScenario,
                BitrixWorkforceDistribution.MissedCallScenario,
                BitrixWorkforceDistribution.NewScenario
            ],
            applied.Select(x => x.Scenario));
        Assert.Equal([10L, 10L, 20L], applied.Select(x => x.SelectedResponsibleId));
        Assert.Equal(3, assignments.Count(x => x.Decision == BitrixWorkforceDecisions.Ignored));
        Assert.Equal(20L, harness.Client.Deals[16002].AssignedById);

        var cursors = await harness.Db.BitrixWorkforceCursors.ToListAsync();
        Assert.Equal(2, cursors.Count);
        Assert.Equal(
            20L,
            Assert.Single(cursors, x => x.Scenario == BitrixWorkforceDistribution.NewScenario)
                .LastAssignedBitrixUserId);
        Assert.Equal(
            10L,
            Assert.Single(cursors, x => x.Scenario == BitrixWorkforceDistribution.MissedCallScenario)
                .LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task WriterMode_LeavingCategoryAndReturning_CreatesNewEntry()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(16003, "NEW");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        harness.Client.Deals[16003] = harness.Client.Deals[16003] with
        {
            CategoryId = 1
        };
        harness.AddEvent(16003, "left-category-16003");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        harness.Client.Deals[16003] = harness.Client.Deals[16003] with
        {
            CategoryId = 0
        };
        harness.AddEvent(16003, "returned-category-16003");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(3, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(10L, assignments[0].SelectedResponsibleId);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Equal("category_mismatch", assignments[1].Scenario);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[2].Decision);
        Assert.Equal(20L, assignments[2].SelectedResponsibleId);
        Assert.Equal(20L, harness.Client.Deals[16003].AssignedById);
        Assert.Equal(
            20L,
            Assert.Single(harness.Db.BitrixWorkforceCursors).LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task WriterMode_TargetOnlyStageOfActiveScenario_PreservesOwner()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_SECONDARY",
            "UC_TARGET_ONLY",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(16004, "UC_SECONDARY");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        Assert.Equal("UC_TARGET_ONLY", harness.Client.Deals[16004].StageId);
        harness.Client.Deals[16004] = harness.Client.Deals[16004] with
        {
            AssignedById = 999
        };
        harness.AddEvent(16004, "target-only-owner-change-16004");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Equal("stage_not_configured", assignments[1].Scenario);
        Assert.Equal(999L, harness.Client.Deals[16004].AssignedById);
        Assert.Single(harness.Client.DealUpdates);
        Assert.Equal(
            10L,
            Assert.Single(harness.Db.BitrixWorkforceCursors).LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task WriterMode_HandledEntryAfterDisabledAndReenabled_DoesNotReassign()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(16005, "NEW");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var configuration = Assert.Single(harness.Db.BitrixWorkforceConfigurations);
        configuration.OperationMode = BitrixWorkforceDistribution.DisabledMode;
        configuration.WriterRulesConfirmed = false;
        configuration.UpdatedAtUtc = configuration.UpdatedAtUtc.AddMinutes(1);
        harness.Client.Deals[16005] = harness.Client.Deals[16005] with
        {
            AssignedById = 999
        };
        harness.AddEvent(16005, "disabled-owner-change-16005");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        configuration.OperationMode = BitrixWorkforceDistribution.WriterMode;
        configuration.WriterRulesConfirmed = true;
        configuration.UpdatedAtUtc = configuration.UpdatedAtUtc.AddMinutes(1);
        harness.AddEvent(16005, "reenabled-owner-change-16005");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(3, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Equal("disabled", assignments[1].Scenario);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[2].Decision);
        Assert.Equal(999L, harness.Client.Deals[16005].AssignedById);
        Assert.Single(harness.Client.DealUpdates);
        Assert.Equal(
            10L,
            Assert.Single(harness.Db.BitrixWorkforceCursors).LastAssignedBitrixUserId);
    }

    [Fact]
    public async Task WriterMode_HandledDeal_WhenRuleIsTemporarilyDisabledAndReenabled_PreservesOwnerAndCursor()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.AddDeal(16009, "NEW");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var originalCursor = Assert.Single(harness.Db.BitrixWorkforceCursors);
        var originalCursorManagerId = originalCursor.LastAssignedBitrixUserId;
        var originalCursorUpdatedAt = originalCursor.LastAssignedAtUtc;
        harness.Client.Deals[16009] = harness.Client.Deals[16009] with
        {
            AssignedById = 999
        };

        var rule = Assert.Single(harness.Db.BitrixWorkforceStageRules);
        rule.IsEnabled = false;
        harness.AddEvent(16009, "rule-disabled-16009");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        rule.IsEnabled = true;
        harness.AddEvent(16009, "rule-reenabled-16009");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(3, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, assignments[0].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Equal("stage_not_configured", assignments[1].Scenario);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[2].Decision);
        Assert.Contains("already handled", assignments[2].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(999L, harness.Client.Deals[16009].AssignedById);
        Assert.Single(harness.Client.DealUpdates);

        var cursor = Assert.Single(harness.Db.BitrixWorkforceCursors);
        Assert.Equal(originalCursorManagerId, cursor.LastAssignedBitrixUserId);
        Assert.Equal(originalCursorUpdatedAt, cursor.LastAssignedAtUtc);
    }

    [Fact]
    public async Task WriterMode_ManagerStatusFailureThenManualOwnerChange_RetryPreservesOwner()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.Client.ManagerStatusFailuresRemaining = 1;
        harness.AddDeal(16010, "NEW");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        Assert.Equal(1, harness.Client.GetDealAttempts);
        Assert.Equal(1, harness.Client.ManagerStatusAttempts);
        Assert.Equal(
            BitrixWorkforceInboxStates.Pending,
            Assert.Single(harness.Db.BitrixDealEventInbox).State);
        var initialAssignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Deferred, initialAssignment.Decision);
        Assert.Null(initialAssignment.PreviousResponsibleId);

        harness.Client.Deals[16010] = harness.Client.Deals[16010] with
        {
            AssignedById = 999
        };
        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignment.Decision);
        Assert.Contains("responsible changed", assignment.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            BitrixWorkforceInboxStates.Completed,
            Assert.Single(harness.Db.BitrixDealEventInbox).State);
        Assert.Equal(999L, harness.Client.Deals[16010].AssignedById);
        Assert.Equal(1, harness.Client.ManagerStatusAttempts);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
        Assert.NotNull(
            Assert.Single(harness.Db.BitrixWorkforceDealStates)
                .ActiveScenarioWriterHandledAtUtc);
    }

    [Fact]
    public async Task WriterMode_ManagerStatusFailuresReachMaxAttempts_EntryGuardBlocksNewWebhook()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true,
            maxAttempts: 2);
        harness.Client.ManagerStatuses =
        [
            new(10, true, "OPENED"),
            new(20, true, "OPENED")
        ];
        harness.Client.ManagerStatusFailuresRemaining = 2;
        harness.AddDeal(16011, "NEW");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();
        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        await harness.Sut.ProcessBatchAsync();

        var failedJob = Assert.Single(harness.Db.BitrixDealEventInbox);
        Assert.Equal(BitrixWorkforceInboxStates.Dead, failedJob.State);
        Assert.Equal(2, failedJob.FailureCount);
        Assert.Equal(2, harness.Client.ManagerStatusAttempts);
        Assert.Equal(
            BitrixWorkforceDecisions.Failed,
            Assert.Single(harness.Db.BitrixWorkforceAssignments).Decision);
        var guardAt = Assert.Single(harness.Db.BitrixWorkforceDealStates)
            .ActiveScenarioWriterHandledAtUtc;
        Assert.NotNull(guardAt);

        harness.Client.Deals[16011] = harness.Client.Deals[16011] with
        {
            AssignedById = 999
        };
        harness.AddEvent(16011, "after-manager-status-dead-letter-16011");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var jobs = await harness.Db.BitrixDealEventInbox
            .OrderBy(x => x.Id)
            .ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.Equal(BitrixWorkforceInboxStates.Dead, jobs[0].State);
        Assert.Equal(BitrixWorkforceInboxStates.Completed, jobs[1].State);
        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Failed, assignments[0].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Contains("already handled", assignments[1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(guardAt, Assert.Single(harness.Db.BitrixWorkforceDealStates)
            .ActiveScenarioWriterHandledAtUtc);
        Assert.Equal(999L, harness.Client.Deals[16011].AssignedById);
        Assert.Equal(2, harness.Client.ManagerStatusAttempts);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
    }

    [Fact]
    public async Task WriterMode_DeferredJob_AfterDisableAndReenableRevision_DoesNotReassign()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "CLOSED")];
        harness.AddDeal(16012, "NEW");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        Assert.Equal(
            BitrixWorkforceDecisions.Deferred,
            Assert.Single(harness.Db.BitrixWorkforceAssignments).Decision);
        harness.Client.Deals[16012] = harness.Client.Deals[16012] with
        {
            AssignedById = 999
        };
        var configuration = Assert.Single(harness.Db.BitrixWorkforceConfigurations);
        configuration.OperationMode = BitrixWorkforceDistribution.DisabledMode;
        configuration.WriterRulesConfirmed = false;
        configuration.UpdatedAtUtc = configuration.UpdatedAtUtc.AddMinutes(1);
        await harness.SaveAsync();
        configuration.OperationMode = BitrixWorkforceDistribution.WriterMode;
        configuration.WriterRulesConfirmed = true;
        configuration.UpdatedAtUtc = configuration.UpdatedAtUtc.AddMinutes(1);
        await harness.SaveAsync();

        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        await harness.Sut.ProcessBatchAsync();

        var originalAssignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, originalAssignment.Decision);
        Assert.Contains("settings changed", originalAssignment.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(
            Assert.Single(harness.Db.BitrixWorkforceDealStates)
                .ActiveScenarioWriterHandledAtUtc);

        harness.AddEvent(16012, "after-deferred-config-revision-16012");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.All(assignments, x => Assert.Equal(BitrixWorkforceDecisions.Ignored, x.Decision));
        Assert.Contains("already handled", assignments[1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(999L, harness.Client.Deals[16012].AssignedById);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
    }

    [Fact]
    public async Task WriterMode_GetDealFailureBeforeOwnerBaseline_RetryPreservesCurrentOwner()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(16013, "NEW");
        harness.Client.GetDealFailuresRemaining = 1;

        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var failedOnce = Assert.Single(harness.Db.BitrixDealEventInbox);
        Assert.Equal(BitrixWorkforceInboxStates.Pending, failedOnce.State);
        Assert.Equal(1, failedOnce.FailureCount);
        Assert.Empty(harness.Db.BitrixWorkforceAssignments);
        harness.Client.Deals[16013] = harness.Client.Deals[16013] with
        {
            AssignedById = 999
        };

        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        await harness.Sut.ProcessBatchAsync();

        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignment.Decision);
        Assert.Contains("original owner", assignment.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            BitrixWorkforceInboxStates.Completed,
            Assert.Single(harness.Db.BitrixDealEventInbox).State);
        Assert.Equal(999L, harness.Client.Deals[16013].AssignedById);
        Assert.Equal(0, harness.Client.ManagerStatusAttempts);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
        Assert.NotNull(
            Assert.Single(harness.Db.BitrixWorkforceDealStates)
                .ActiveScenarioWriterHandledAtUtc);
    }

    [Fact]
    public async Task WriterMode_UnresolvableWebhook_AutoDisablesWithoutRetryOrWrite()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(16018, "NEW");
        harness.Client.Deals[16018] = harness.Client.Deals[16018] with
        {
            AssignedById = 999
        };
        Assert.Single(harness.Db.BitrixInstances).WebhookUrlProtected =
            "invalid-protected-webhook";
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var job = Assert.Single(harness.Db.BitrixDealEventInbox);
        Assert.Equal(BitrixWorkforceInboxStates.Completed, job.State);
        Assert.Equal(0, job.FailureCount);
        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignment.Decision);
        Assert.Equal("duplicate_writer_configuration", assignment.Scenario);
        var configuration = Assert.Single(harness.Db.BitrixWorkforceConfigurations);
        Assert.Equal(BitrixWorkforceDistribution.DisabledMode, configuration.OperationMode);
        Assert.False(configuration.WriterRulesConfirmed);
        Assert.Equal(999L, harness.Client.Deals[16018].AssignedById);
        Assert.Equal(0, harness.Client.GetDealAttempts);
        Assert.Equal(0, harness.Client.ManagerStatusAttempts);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
    }

    [Fact]
    public async Task DisabledEarlyReturn_WithExistingDeferredAssignment_PreservesEntryGuard()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "CLOSED")];
        harness.AddDeal(16014, "NEW");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var deferredAssignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Deferred, deferredAssignment.Decision);
        harness.Client.Deals[16014] = harness.Client.Deals[16014] with
        {
            AssignedById = 999
        };
        var configuration = Assert.Single(harness.Db.BitrixWorkforceConfigurations);
        configuration.OperationMode = BitrixWorkforceDistribution.DisabledMode;
        configuration.WriterRulesConfirmed = false;
        configuration.UpdatedAtUtc = configuration.UpdatedAtUtc.AddMinutes(1);
        await harness.SaveAsync();

        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        await harness.Sut.ProcessBatchAsync();

        var disabledAssignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, disabledAssignment.Decision);
        Assert.Contains("disabled", disabledAssignment.Reason, StringComparison.OrdinalIgnoreCase);
        var guardAt = Assert.Single(harness.Db.BitrixWorkforceDealStates)
            .ActiveScenarioWriterHandledAtUtc;
        Assert.NotNull(guardAt);

        configuration = Assert.Single(harness.Db.BitrixWorkforceConfigurations);
        configuration.OperationMode = BitrixWorkforceDistribution.WriterMode;
        configuration.WriterRulesConfirmed = true;
        configuration.UpdatedAtUtc = configuration.UpdatedAtUtc.AddMinutes(1);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddEvent(16014, "after-disabled-early-return-16014");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.All(assignments, x => Assert.Equal(BitrixWorkforceDecisions.Ignored, x.Decision));
        Assert.Contains("already handled", assignments[1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(guardAt, Assert.Single(harness.Db.BitrixWorkforceDealStates)
            .ActiveScenarioWriterHandledAtUtc);
        Assert.Equal(999L, harness.Client.Deals[16014].AssignedById);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
    }

    [Fact]
    public async Task WriterMode_DeferredJob_OwnerChangeWebhookBeforeRetry_SetsGuardWithoutReassigning()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "CLOSED")];
        harness.AddDeal(16015, "NEW");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var deferredAssignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Deferred, deferredAssignment.Decision);
        Assert.Null(deferredAssignment.PreviousResponsibleId);
        harness.Client.Deals[16015] = harness.Client.Deals[16015] with
        {
            AssignedById = 999
        };
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddEvent(16015, "owner-change-before-deferred-retry-16015");
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var jobsBeforeOriginalRetry = await harness.Db.BitrixDealEventInbox
            .OrderBy(x => x.Id)
            .ToListAsync();
        Assert.Equal(BitrixWorkforceInboxStates.Pending, jobsBeforeOriginalRetry[0].State);
        Assert.Equal(BitrixWorkforceInboxStates.Completed, jobsBeforeOriginalRetry[1].State);
        var assignmentsBeforeOriginalRetry = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(BitrixWorkforceDecisions.Deferred, assignmentsBeforeOriginalRetry[0].Decision);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignmentsBeforeOriginalRetry[1].Decision);
        Assert.Contains(
            "already owns",
            assignmentsBeforeOriginalRetry[1].Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(
            Assert.Single(harness.Db.BitrixWorkforceDealStates)
                .ActiveScenarioWriterHandledAtUtc);
        Assert.Equal(999L, harness.Client.Deals[16015].AssignedById);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);

        harness.Time.Now = harness.Time.Now.AddMinutes(1);
        await harness.Sut.ProcessBatchAsync();

        var completedAssignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.All(completedAssignments, x =>
            Assert.Equal(BitrixWorkforceDecisions.Ignored, x.Decision));
        Assert.Equal(1, harness.Client.ManagerStatusAttempts);
        Assert.Equal(999L, harness.Client.Deals[16015].AssignedById);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
    }

    [Theory]
    [InlineData(BitrixWorkforceInboxStates.Dead)]
    [InlineData(BitrixWorkforceInboxStates.Completed)]
    public async Task WriterMode_PriorUnprotectedFailure_NewWebhookCreatesTombstoneAndGuard(
        string priorState)
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.SetDeal(16016, "NEW");
        harness.Client.Deals[16016] = harness.Client.Deals[16016] with
        {
            AssignedById = 999
        };
        harness.AddEvent(16016, $"prior-unprotected-{priorState}-16016");
        await harness.SaveAsync();
        var priorJob = Assert.Single(harness.Db.BitrixDealEventInbox);
        priorJob.State = priorState;
        priorJob.FailureCount = 1;
        priorJob.LastError = "Synthetic failure before baseline.";
        priorJob.CompletedAtUtc = harness.Time.GetUtcNow().UtcDateTime;
        harness.AddEvent(16016, $"after-unprotected-{priorState}-16016");
        await harness.SaveAsync();

        Assert.Empty(harness.Db.BitrixWorkforceAssignments);
        Assert.Empty(harness.Db.BitrixWorkforceDealStates);
        await harness.Sut.ProcessBatchAsync();

        var jobs = await harness.Db.BitrixDealEventInbox
            .OrderBy(x => x.Id)
            .ToListAsync();
        Assert.Equal(priorState, jobs[0].State);
        Assert.Equal(BitrixWorkforceInboxStates.Completed, jobs[1].State);
        var assignments = await harness.Db.BitrixWorkforceAssignments
            .OrderBy(x => x.InboxId)
            .ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[0].Decision);
        Assert.Contains("tombstone", assignments[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignments[1].Decision);
        Assert.Contains("previous attempt failed", assignments[1].Reason, StringComparison.OrdinalIgnoreCase);
        var dealState = Assert.Single(harness.Db.BitrixWorkforceDealStates);
        Assert.Equal(BitrixWorkforceDistribution.NewScenario, dealState.ActiveScenario);
        Assert.NotNull(dealState.ActiveScenarioWriterHandledAtUtc);
        Assert.Equal(999L, harness.Client.Deals[16016].AssignedById);
        Assert.Equal(0, harness.Client.ManagerStatusAttempts);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
    }

    [Fact]
    public async Task WriterMode_LegacyDuplicatePortalAndCategory_AutoDisablesWithoutWriting()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.WriterMode,
            BitrixWorkforceDistribution.NewScenario,
            "NEW",
            "NEW",
            usesMorningWindow: false,
            writerConfirmed: true);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(16017, "NEW");
        harness.Client.Deals[16017] = harness.Client.Deals[16017] with
        {
            AssignedById = 999
        };
        var competitorId = Guid.NewGuid();
        var officeId = Assert.Single(harness.Db.Offices).Id;
        harness.Db.BitrixInstances.Add(new BitrixInstanceEntity
        {
            Id = competitorId,
            OfficeId = officeId,
            Name = "Legacy duplicate writer",
            PortalHost = "example.bitrix24.ru",
            WebhookUrlProtected = "legacy-protected-webhook",
            IntegrationSettingsJson = "{}",
            IsEnabled = true,
            CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime,
            UpdatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
        });
        harness.Db.BitrixWorkforceConfigurations.Add(new BitrixWorkforceConfigurationEntity
        {
            BitrixInstanceId = competitorId,
            OperationMode = BitrixWorkforceDistribution.WriterMode,
            DealCategoryId = 0,
            TimeZoneId = "Europe/Moscow",
            WriterRulesConfirmed = true,
            UpdatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
        });
        await harness.SaveAsync();

        await harness.Sut.ProcessBatchAsync();

        var currentConfiguration = await harness.Db.BitrixWorkforceConfigurations
            .SingleAsync(x => x.BitrixInstanceId == harness.InstanceId);
        var competingConfiguration = await harness.Db.BitrixWorkforceConfigurations
            .SingleAsync(x => x.BitrixInstanceId == competitorId);
        Assert.Equal(BitrixWorkforceDistribution.DisabledMode, currentConfiguration.OperationMode);
        Assert.False(currentConfiguration.WriterRulesConfirmed);
        Assert.Equal(BitrixWorkforceDistribution.WriterMode, competingConfiguration.OperationMode);
        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDecisions.Ignored, assignment.Decision);
        Assert.Equal("duplicate_writer_configuration", assignment.Scenario);
        Assert.Contains("another enabled", assignment.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(999L, harness.Client.Deals[16017].AssignedById);
        Assert.Equal(0, harness.Client.GetDealAttempts);
        Assert.Equal(0, harness.Client.ManagerStatusAttempts);
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Empty(harness.Db.BitrixWorkforceCursors);
    }

    [Fact]
    public async Task ShadowMorningState_DoesNotReserveOrAdvanceWriterMorningState()
    {
        await using var harness = await Harness.CreateAsync(
            BitrixWorkforceDistribution.ShadowMode,
            BitrixWorkforceDistribution.MissedCallScenario,
            "UC_MISSED",
            "UC_MISSED",
            usesMorningWindow: true,
            reserveMinutes: 120,
            releasePercent: 50);
        harness.Client.ManagerStatuses = [new(10, true, "OPENED")];
        harness.AddDeal(16006, "UC_MISSED");
        harness.AddDeal(16007, "UC_MISSED");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        Assert.Equal(
            BitrixWorkforceDecisions.Assigned,
            await harness.Db.BitrixWorkforceAssignments
                .Where(x => x.DealId == 16006)
                .Select(x => x.Decision)
                .SingleAsync());
        Assert.Equal(
            BitrixWorkforceDecisions.Reserved,
            await harness.Db.BitrixWorkforceAssignments
                .Where(x => x.DealId == 16007)
                .Select(x => x.Decision)
                .SingleAsync());

        harness.Client.Deals[16006] = harness.Client.Deals[16006] with { StageId = "WON" };
        harness.Client.Deals[16007] = harness.Client.Deals[16007] with { StageId = "WON" };
        var configuration = Assert.Single(harness.Db.BitrixWorkforceConfigurations);
        configuration.OperationMode = BitrixWorkforceDistribution.WriterMode;
        configuration.WriterRulesConfirmed = true;
        configuration.UpdatedAtUtc = configuration.UpdatedAtUtc.AddMinutes(1);
        harness.AddDeal(16008, "UC_MISSED");
        await harness.SaveAsync();
        await harness.Sut.ProcessBatchAsync();

        var writerAssignment = await harness.Db.BitrixWorkforceAssignments
            .SingleAsync(x => x.DealId == 16008);
        Assert.Equal(BitrixWorkforceDecisions.Assigned, writerAssignment.Decision);
        Assert.Equal(10L, writerAssignment.SelectedResponsibleId);
        Assert.Equal(10L, harness.Client.Deals[16008].AssignedById);

        var morningStates = await harness.Db.BitrixWorkforceMorningStates.ToListAsync();
        Assert.Equal(2, morningStates.Count);
        var shadowState = Assert.Single(
            morningStates,
            x => x.OperationMode == BitrixWorkforceDistribution.ShadowMode);
        var writerState = Assert.Single(
            morningStates,
            x => x.OperationMode == BitrixWorkforceDistribution.WriterMode);
        Assert.Equal(1, shadowState.InitialReleasedCount);
        Assert.Equal(1, writerState.InitialReleasedCount);

        var cursors = await harness.Db.BitrixWorkforceCursors.ToListAsync();
        Assert.Equal(2, cursors.Count);
        Assert.All(cursors, x => Assert.Equal(10L, x.LastAssignedBitrixUserId));
        Assert.Contains(cursors, x => x.OperationMode == BitrixWorkforceDistribution.ShadowMode);
        Assert.Contains(cursors, x => x.OperationMode == BitrixWorkforceDistribution.WriterMode);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly DbContextOptions<OrbitaDbContext> _dbOptions;
        private readonly WebhookSecretProtector _protector;
        private readonly Guid _officeId;
        private readonly Guid _instanceId;

        public OrbitaDbContext Db { get; }
        public StubBitrixWorkforceClient Client { get; }
        public FakeTimeProvider Time { get; }
        public BitrixWorkforceProcessor Sut { get; }
        public Guid InstanceId => _instanceId;

        private Harness(
            DbContextOptions<OrbitaDbContext> dbOptions,
            OrbitaDbContext db,
            WebhookSecretProtector protector,
            Guid officeId,
            Guid instanceId,
            StubBitrixWorkforceClient client,
            FakeTimeProvider time,
            BitrixWorkforceProcessor sut)
        {
            _dbOptions = dbOptions;
            Db = db;
            _protector = protector;
            _officeId = officeId;
            _instanceId = instanceId;
            Client = client;
            Time = time;
            Sut = sut;
        }

        public static async Task<Harness> CreateAsync(
            string mode,
            string scenario,
            string sourceStage,
            string targetStage,
            bool usesMorningWindow,
            bool writerConfirmed = false,
            int reserveMinutes = 120,
            bool syncContactOwner = false,
            int maxAttempts = 10,
            decimal releasePercent = 50,
            int integrationResponsibleId = 0)
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            var db = new OrbitaDbContext(options);
            var provider = new EphemeralDataProtectionProvider();
            var protector = new WebhookSecretProtector(provider);
            var defaultBitrix = new OrbitaBitrixSettings
            {
                DealIdempotencyUfCode = "UF_IDEMPOTENCY",
                DealAgeUfCode = "UF_AGE",
                DealProfessionUfCode = "UF_PROFESSION",
                DealCityUfCode = "UF_CITY"
            };
            var audit = new PanelAuditService(db);
            var instances = new BitrixInstanceService(
                db,
                protector,
                null!,
                Options.Create(defaultBitrix),
                audit);
            var client = new StubBitrixWorkforceClient();
            var time = new FakeTimeProvider(
                new DateTimeOffset(2026, 7, 30, 6, 0, 0, TimeSpan.Zero));
            var processor = new BitrixWorkforceProcessor(
                db,
                instances,
                client,
                Options.Create(defaultBitrix),
                Options.Create(new BitrixWorkforceOptions
                {
                    BatchSize = 50,
                    LeaseSeconds = 60
                }),
                time,
                NullLogger<BitrixWorkforceProcessor>.Instance);

            var officeId = Guid.NewGuid();
            var instanceId = Guid.NewGuid();
            db.Offices.Add(new OfficeEntity
            {
                Id = officeId,
                Name = "Office",
                RegistrationSecretHash = "hash",
                CreatedAtUtc = time.GetUtcNow().UtcDateTime
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
                    ResponsibleId = integrationResponsibleId,
                    DealIdempotencyUfCode = "UF_IDEMPOTENCY",
                    DealAgeUfCode = "UF_AGE",
                    DealProfessionUfCode = "UF_PROFESSION",
                    DealCityUfCode = "UF_CITY"
                }.Serialize(),
                IsEnabled = true,
                CreatedAtUtc = time.GetUtcNow().UtcDateTime,
                UpdatedAtUtc = time.GetUtcNow().UtcDateTime
            });
            db.BitrixWorkforceConfigurations.Add(new BitrixWorkforceConfigurationEntity
            {
                BitrixInstanceId = instanceId,
                OperationMode = mode,
                DealCategoryId = 0,
                TimeZoneId = "Europe/Moscow",
                MorningWindowStartMinutes = 480,
                MorningWindowEndMinutes = 660,
                LateJoinReserveMinutes = reserveMinutes,
                SingleManagerInitialReleasePercent = releasePercent,
                RetryDelaySeconds = 60,
                MaxAttempts = maxAttempts,
                PreserveManualNewOwner = true,
                SyncContactOwner = syncContactOwner,
                FillOnlyEmptyAvitoFields = true,
                WriterRulesConfirmed = writerConfirmed,
                UpdatedAtUtc = time.GetUtcNow().UtcDateTime
            });
            db.BitrixWorkforceManagers.AddRange(
                new BitrixWorkforceManagerEntity
                {
                    Id = Guid.NewGuid(),
                    BitrixInstanceId = instanceId,
                    BitrixUserId = 10,
                    SortOrder = 0
                },
                new BitrixWorkforceManagerEntity
                {
                    Id = Guid.NewGuid(),
                    BitrixInstanceId = instanceId,
                    BitrixUserId = 20,
                    SortOrder = 1
                });
            db.BitrixWorkforceStageRules.Add(new BitrixWorkforceStageRuleEntity
            {
                Id = Guid.NewGuid(),
                BitrixInstanceId = instanceId,
                Scenario = scenario,
                SourceStageId = sourceStage,
                TargetStageId = targetStage,
                UsesMorningWindow = usesMorningWindow,
                SortOrder = 0,
                IsEnabled = true
            });
            await db.SaveChangesAsync();
            return new Harness(
                options,
                db,
                protector,
                officeId,
                instanceId,
                client,
                time,
                processor);
        }

        public void AddDeal(
            long dealId,
            string stage,
            string comments = "")
        {
            SetDeal(dealId, stage, comments);
            AddEvent(dealId, $"event-{dealId}");
        }

        public void SetDeal(
            long dealId,
            string stage,
            string comments = "")
        {
            Client.Deals[dealId] = new BitrixWorkforceDeal(
                dealId,
                0,
                stage,
                AssignedById: null,
                CreatedById: 999,
                ModifiedById: 999,
                comments,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        }

        public void AddEvent(long dealId, string eventKey)
        {
            Db.BitrixDealEventInbox.Add(new BitrixDealEventInboxEntity
            {
                BitrixInstanceId = _instanceId,
                EventName = "ONCRMDEALUPDATE",
                DealId = dealId,
                EventKey = eventKey,
                ReceivedAtUtc = Time.GetUtcNow().UtcDateTime,
                State = BitrixWorkforceInboxStates.Pending,
                NextAttemptAtUtc = Time.GetUtcNow().UtcDateTime
            });
        }

        public void AddRule(
            string scenario,
            string sourceStage,
            string targetStage,
            bool usesMorningWindow,
            int sortOrder)
        {
            Db.BitrixWorkforceStageRules.Add(new BitrixWorkforceStageRuleEntity
            {
                Id = Guid.NewGuid(),
                BitrixInstanceId = _instanceId,
                Scenario = scenario,
                SourceStageId = sourceStage,
                TargetStageId = targetStage,
                UsesMorningWindow = usesMorningWindow,
                SortOrder = sortOrder,
                IsEnabled = true
            });
        }

        public void AddCursor(string scenario, string operationMode, long? lastManagerId)
        {
            Db.BitrixWorkforceCursors.Add(new BitrixWorkforceCursorEntity
            {
                BitrixInstanceId = _instanceId,
                Scenario = scenario,
                OperationMode = operationMode,
                LastAssignedBitrixUserId = lastManagerId,
                LastAssignedAtUtc = lastManagerId is null
                    ? null
                    : Time.GetUtcNow().UtcDateTime
            });
        }

        public Task SaveAsync() => Db.SaveChangesAsync();

        public async Task MutateConfigurationOutOfBandAsync(
            Action<BitrixWorkforceConfigurationEntity> mutate)
        {
            await using var otherDb = new OrbitaDbContext(_dbOptions);
            var configuration = await otherDb.BitrixWorkforceConfigurations
                .SingleAsync(x => x.BitrixInstanceId == _instanceId);
            mutate(configuration);
            await otherDb.SaveChangesAsync();
        }

        public async Task MutateInstanceOutOfBandAsync(
            Action<BitrixInstanceEntity> mutate)
        {
            await using var otherDb = new OrbitaDbContext(_dbOptions);
            var instance = await otherDb.BitrixInstances
                .SingleAsync(x => x.Id == _instanceId);
            mutate(instance);
            await otherDb.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubBitrixWorkforceClient : IBitrixWorkforceClient
    {
        public Dictionary<long, BitrixWorkforceDeal> Deals { get; } = [];
        public Dictionary<long, Queue<BitrixWorkforceDeal>> DealReadSequences { get; } = [];
        public Dictionary<long, long?> ContactOwners { get; } = [];
        public IReadOnlyList<BitrixWorkforceManagerStatus> ManagerStatuses { get; set; } = [];
        public IReadOnlyList<long> ContactIds { get; set; } = [];
        public List<(long DealId, IReadOnlyDictionary<string, object?> Fields)> DealUpdates { get; } = [];
        public List<(long ContactId, long ResponsibleId)> ContactUpdates { get; } = [];
        public int GetDealAttempts { get; private set; }
        public int DealUpdateAttempts { get; private set; }
        public int DealUpdateFailuresRemaining { get; set; }
        public int DealUpdateResponseFailuresRemaining { get; set; }
        public int ContactUpdateFailuresRemaining { get; set; }
        public int GetDealFailuresRemaining { get; set; }
        public int ManagerStatusAttempts { get; private set; }
        public int ManagerStatusFailuresRemaining { get; set; }
        public Func<Task>? BeforeManagerStatusesReturnAsync { get; set; }
        public Func<int, Task>? BeforeDealReadReturnAsync { get; set; }
        public Func<Task>? AfterDealUpdateAsync { get; set; }

        public async Task<BitrixWorkforceDeal> GetDealAsync(
            string webhookUrl,
            long dealId,
            CancellationToken ct)
        {
            GetDealAttempts++;
            if (GetDealFailuresRemaining > 0)
            {
                GetDealFailuresRemaining--;
                throw new HttpRequestException("Synthetic deal-read failure.");
            }

            if (BeforeDealReadReturnAsync is not null)
            {
                await BeforeDealReadReturnAsync(GetDealAttempts);
            }

            if (DealReadSequences.TryGetValue(dealId, out var sequence)
                && sequence.Count > 0)
            {
                return sequence.Dequeue();
            }

            return Deals[dealId];
        }

        public void SetDealReadSequence(
            long dealId,
            params BitrixWorkforceDeal[] deals) =>
            DealReadSequences[dealId] = new Queue<BitrixWorkforceDeal>(deals);

        public async Task<IReadOnlyList<BitrixWorkforceManagerStatus>> GetManagerStatusesAsync(
            string webhookUrl,
            IReadOnlyList<long> managerIds,
            CancellationToken ct)
        {
            ManagerStatusAttempts++;
            if (BeforeManagerStatusesReturnAsync is not null)
            {
                await BeforeManagerStatusesReturnAsync();
            }

            if (ManagerStatusFailuresRemaining > 0)
            {
                ManagerStatusFailuresRemaining--;
                throw new HttpRequestException("Synthetic manager-status failure.");
            }

            return ManagerStatuses;
        }

        public Task<IReadOnlyList<long>> GetDealContactIdsAsync(
            string webhookUrl,
            long dealId,
            CancellationToken ct) =>
            Task.FromResult(ContactIds);

        public Task<long?> GetContactOwnerIdAsync(
            string webhookUrl,
            long contactId,
            CancellationToken ct) =>
            Task.FromResult(
                ContactOwners.TryGetValue(contactId, out var ownerId)
                    ? ownerId
                    : null);

        public async Task UpdateDealAsync(
            string webhookUrl,
            long dealId,
            IReadOnlyDictionary<string, object?> fields,
            CancellationToken ct)
        {
            DealUpdateAttempts++;
            if (DealUpdateFailuresRemaining > 0)
            {
                DealUpdateFailuresRemaining--;
                throw new HttpRequestException("Synthetic deal update failure.");
            }

            DealUpdates.Add((
                dealId,
                new Dictionary<string, object?>(fields, StringComparer.OrdinalIgnoreCase)));
            if (Deals.TryGetValue(dealId, out var deal))
            {
                var updatedFields = new Dictionary<string, string?>(
                    deal.Fields,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var field in fields)
                {
                    updatedFields[field.Key] = field.Value?.ToString();
                }

                Deals[dealId] = deal with
                {
                    StageId = fields.TryGetValue("STAGE_ID", out var stage)
                        ? stage?.ToString() ?? deal.StageId
                        : deal.StageId,
                    AssignedById = fields.TryGetValue("ASSIGNED_BY_ID", out var responsible)
                                   && long.TryParse(
                                       responsible?.ToString(),
                                       out var responsibleId)
                        ? responsibleId
                        : deal.AssignedById,
                    Fields = updatedFields
                };
            }

            if (DealUpdateResponseFailuresRemaining > 0)
            {
                DealUpdateResponseFailuresRemaining--;
                throw new HttpRequestException(
                    "Synthetic response failure after the deal update was applied.");
            }

            if (AfterDealUpdateAsync is not null)
            {
                await AfterDealUpdateAsync();
            }
        }

        public Task UpdateContactOwnerAsync(
            string webhookUrl,
            long contactId,
            long responsibleId,
            CancellationToken ct)
        {
            if (ContactUpdateFailuresRemaining > 0)
            {
                ContactUpdateFailuresRemaining--;
                throw new HttpRequestException("Synthetic contact update failure.");
            }

            ContactUpdates.Add((contactId, responsibleId));
            ContactOwners[contactId] = responsibleId;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BitrixWorkforceDealRevision>> ListDealsAsync(
            string webhookUrl,
            int categoryId,
            IReadOnlyCollection<string> stageIds,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BitrixWorkforceDealRevision>>(
                Deals.Values
                    .Where(x => x.CategoryId == categoryId && stageIds.Contains(x.StageId))
                    .Select(x => new BitrixWorkforceDealRevision(
                        x.Id,
                        x.StageId,
                        x.Fields.TryGetValue("DATE_MODIFY", out var revision)
                            ? revision ?? string.Empty
                            : string.Empty))
                    .ToList());

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

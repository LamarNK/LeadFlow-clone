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

        var update = Assert.Single(harness.Client.DealUpdates);
        Assert.Equal("UC_FU2T4L", update.Fields["STAGE_ID"]);
        var assignment = Assert.Single(harness.Db.BitrixWorkforceAssignments);
        Assert.Equal(BitrixWorkforceDistribution.MissedCallScenario, assignment.Scenario);
        Assert.Equal("UC_ENSD7E", assignment.FromStageId);
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
    public async Task DeferredDuplicateEvent_AfterFirstAssignmentCompletes_IsIgnored()
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
        Assert.Equal(BitrixWorkforceDecisions.Deferred, firstPass[1].Decision);

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
            usesMorningWindow: false);
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
    public async Task WriterMode_ConfigurationChangeDuringSelection_CancelsStaleAssignment()
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
        Assert.Empty(harness.Client.DealUpdates);
        Assert.Equal(
            BitrixWorkforceInboxStates.Completed,
            Assert.Single(harness.Db.BitrixDealEventInbox).State);
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
            decimal releasePercent = 50)
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
        public IReadOnlyList<BitrixWorkforceManagerStatus> ManagerStatuses { get; set; } = [];
        public IReadOnlyList<long> ContactIds { get; set; } = [];
        public List<(long DealId, IReadOnlyDictionary<string, object?> Fields)> DealUpdates { get; } = [];
        public List<(long ContactId, long ResponsibleId)> ContactUpdates { get; } = [];
        public int GetDealAttempts { get; private set; }
        public int DealUpdateAttempts { get; private set; }
        public int DealUpdateFailuresRemaining { get; set; }
        public int ContactUpdateFailuresRemaining { get; set; }
        public Func<Task>? BeforeManagerStatusesReturnAsync { get; set; }
        public Func<int, Task>? BeforeDealReadReturnAsync { get; set; }
        public Func<Task>? AfterDealUpdateAsync { get; set; }

        public async Task<BitrixWorkforceDeal> GetDealAsync(
            string webhookUrl,
            long dealId,
            CancellationToken ct)
        {
            GetDealAttempts++;
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
            if (BeforeManagerStatusesReturnAsync is not null)
            {
                await BeforeManagerStatusesReturnAsync();
            }

            return ManagerStatuses;
        }

        public Task<IReadOnlyList<long>> GetDealContactIdsAsync(
            string webhookUrl,
            long dealId,
            CancellationToken ct) =>
            Task.FromResult(ContactIds);

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

using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed partial class CrmAnalyticsQueryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OfficeOneId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OfficeTwoId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private const string ManagerOneId = "analytics-manager-one";
    private const string ManagerTwoId = "analytics-manager-two";
    private const string FormerManagerId = "analytics-former-manager";

    [Fact]
    public async Task GetAsync_BuildsCohortMetricsPeriodCloseReasonsFunnelAndCurrentManagerLoad()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(OfficeOneId, "Основной", ["Новый", "Звонок", "Анкета"]);
        harness.AddOffice(OfficeTwoId, "Сибирь", ["Лид", "Финал"]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 2, onShift: true);
        harness.AddManager(ManagerTwoId, OfficeOneId, "Борис", capacity: 5, onShift: false);

        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);
        harness.AddShift(ManagerOneId, OfficeOneId, fromUtc.AddHours(-1), fromUtc.AddHours(8));
        harness.AddShift(ManagerTwoId, OfficeOneId, fromUtc.AddDays(1), fromUtc.AddDays(1).AddHours(8));
        var active = NewCard(OfficeOneId, ManagerOneId, "Звонок", fromUtc, activeLoad: true);
        var successful = NewCard(
            OfficeOneId,
            ManagerTwoId,
            "Новый",
            fromUtc.AddDays(1),
            isClosed: true,
            closeReason: CrmCloseReasons.Success);
        var refused = NewCard(
            OfficeOneId,
            managerUserId: null,
            "Новый",
            fromUtc.AddDays(2),
            isClosed: true,
            closeReason: CrmCloseReasons.NotRelevant,
            activeLoad: false);
        var atExclusiveBoundary = NewCard(OfficeOneId, ManagerOneId, "Новый", toUtc);
        var oldCurrent = NewCard(OfficeOneId, ManagerOneId, "Анкета", fromUtc.AddDays(-2), activeLoad: true);
        var robotCurrent = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmManagerLoadRules.RobotStage,
            fromUtc.AddDays(-3),
            activeLoad: true);
        var otherOffice = NewCard(OfficeTwoId, "other-manager", "Лид", fromUtc.AddHours(1));
        harness.Db.CrmCandidateCards.AddRange(
            active,
            successful,
            refused,
            atExclusiveBoundary,
            oldCurrent,
            robotCurrent,
            otherOffice);

        // The card reached the last stage and was later returned to the first one.
        // The configuration suffix is emitted by CrmWorkspaceService when a funnel changes.
        harness.Db.CrmCandidateHistory.AddRange(
            NewStageHistory(successful.Id, "Новый → Анкета (воронка обновлена)", fromUtc.AddDays(1).AddHours(1)),
            NewStageHistory(successful.Id, "Анкета → Новый", fromUtc.AddDays(1).AddHours(2)),
            NewCloseHistory(
                successful.Id,
                CrmCloseReasons.Success,
                fromUtc.AddDays(1).AddHours(3),
                ManagerTwoId,
                "Борис"),
            NewCloseHistory(
                refused.Id,
                CrmCloseReasons.NotRelevant,
                fromUtc.AddDays(2).AddHours(1),
                "admin",
                "Администратор"));

        harness.Db.CrmTasks.AddRange(
            NewTask(OfficeOneId, ManagerOneId, CrmTaskStatuses.Open, Now.UtcDateTime.AddMinutes(-1)),
            NewTask(OfficeOneId, ManagerOneId, CrmTaskStatuses.Open, Now.UtcDateTime),
            NewTask(OfficeOneId, ManagerOneId, CrmTaskStatuses.Completed, Now.UtcDateTime.AddDays(-1)));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId));

        Assert.Equal(CrmAnalyticsQueryOutcome.Success, result.Outcome);
        var data = Assert.IsType<CrmAnalyticsDto>(result.Data);
        Assert.Equal(3, data.Cards.Received);
        Assert.Equal(2, data.Cards.Assigned);
        Assert.Equal(1, data.Cards.Active);
        Assert.Equal(2, data.Cards.Closed);
        Assert.Equal(1, data.Cards.SuccessfulClosed);
        Assert.Equal(66.67, data.Cards.AssignmentRatePercent);
        Assert.Equal(33.33, data.Cards.SuccessRatePercent);
        Assert.Equal(50, data.Cards.SuccessAmongClosedPercent);

        var reasons = data.CloseReasons.ToDictionary(x => x.Reason, StringComparer.Ordinal);
        Assert.Equal(1, reasons[CrmCloseReasons.Success].Count);
        Assert.Equal(1, reasons[CrmCloseReasons.NotRelevant].Count);
        Assert.Equal(2, reasons.Values.Sum(x => x.Count));
        var periodActivity = Assert.IsType<CrmAnalyticsPeriodActivityDto>(data.PeriodActivity);
        Assert.Equal(2, periodActivity.ClosedCards);
        Assert.Equal(1, periodActivity.SuccessfulClosedCards);
        Assert.Equal(1, periodActivity.ClosedCardsOutsideShift);

        var funnel = Assert.Single(data.Funnels);
        Assert.Equal(3, funnel.Received);
        Assert.Collection(
            funnel.Stages,
            stage =>
            {
                Assert.Equal("Новый", stage.Stage);
                Assert.Equal(1, stage.ReachedCount);
                Assert.Equal(2, stage.CurrentCount);
            },
            stage =>
            {
                Assert.Equal("Звонок", stage.Stage);
                Assert.Equal(0, stage.ReachedCount);
                Assert.Equal(1, stage.CurrentCount);
                Assert.Equal(0, stage.ConversionFromPreviousPercent);
            },
            stage =>
            {
                Assert.Equal("Анкета", stage.Stage);
                Assert.Equal(0, stage.ReachedCount);
                Assert.Equal(0, stage.CurrentCount);
                Assert.Equal(0, stage.ConversionFromPreviousPercent);
            });

        var manager = Assert.Single(data.Managers, x => x.UserId == ManagerOneId);
        // Current load deliberately includes cards outside the selected cohort,
        // including the card created exactly at the cohort's exclusive boundary.
        Assert.Equal(4, manager.CurrentAssignedCards);
        Assert.Equal(3, manager.ActiveLoad);
        Assert.Equal(150, manager.CapacityUtilizationPercent);
        Assert.Equal(1, manager.CardsInPeriod);
        Assert.Equal(3, manager.TasksTotal);
        Assert.Equal(2, manager.OpenTasks);
        Assert.Equal(1, manager.OverdueTasks);
    }

    [Fact]
    public async Task GetAsync_ConfiguredAlternateEntryStageIsShownAsReceiptSourceNotConversionStep()
    {
        const string primaryEntry = "Лид";
        const string alternateEntry = "Лид(Важный)";
        const string nextStage = "НДЗ 73";
        await using var harness = await Harness.CreateAsync(
            Now,
            new Dictionary<Guid, string[]>
            {
                [OfficeOneId] = [alternateEntry]
            });
        harness.AddOffice(OfficeOneId, "4 офис", [primaryEntry, alternateEntry, nextStage]);

        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);
        var primaryCurrent = NewCard(OfficeOneId, null, primaryEntry, fromUtc.AddHours(1));
        var alternateCurrent = NewCard(OfficeOneId, null, alternateEntry, fromUtc.AddHours(2));
        var primaryMoved = NewCard(OfficeOneId, null, nextStage, fromUtc.AddHours(3));
        var alternateMoved = NewCard(OfficeOneId, null, nextStage, fromUtc.AddHours(4));
        primaryCurrent.EntryStage = primaryEntry;
        primaryMoved.EntryStage = primaryEntry;
        alternateCurrent.EntryStage = alternateEntry;
        alternateMoved.EntryStage = alternateEntry;
        harness.Db.CrmCandidateCards.AddRange(
            primaryCurrent,
            alternateCurrent,
            primaryMoved,
            alternateMoved);
        harness.Db.CrmCandidateHistory.AddRange(
            NewStageHistory(primaryMoved.Id, $"{primaryEntry} → {nextStage}", fromUtc.AddHours(5)),
            NewStageHistory(alternateMoved.Id, $"{alternateEntry} → {nextStage}", fromUtc.AddHours(6)));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId));

        var funnel = Assert.Single(Assert.IsType<CrmAnalyticsDto>(result.Data).Funnels);
        var sources = Assert.IsAssignableFrom<IReadOnlyList<CrmAnalyticsEntrySourceDto>>(funnel.EntrySources);
        Assert.Collection(
            sources,
            source =>
            {
                Assert.Equal(primaryEntry, source.Stage);
                Assert.Equal(2, source.Count);
                Assert.Equal(50, source.PercentOfReceived);
            },
            source =>
            {
                Assert.Equal(alternateEntry, source.Stage);
                Assert.Equal(2, source.Count);
                Assert.Equal(50, source.PercentOfReceived);
            });
        // Board order remains intact, but initial creation is not a translation.
        Assert.Equal(new[] { primaryEntry, alternateEntry, nextStage }, funnel.Stages.Select(x => x.Stage));
        Assert.Equal(0, funnel.Stages[0].ReachedCount);
        Assert.Equal(2, funnel.Stages[0].CreatedCount);
        Assert.Equal(0, funnel.Stages[1].ReachedCount);
        Assert.Equal(2, funnel.Stages[1].CreatedCount);
        Assert.Equal(2, funnel.Stages[2].ReachedCount);
        Assert.Equal(50, funnel.Stages[2].ConversionFromReceivedPercent);
    }

    [Fact]
    public async Task GetAsync_CloseEventsRemainVisibleAfterReopening()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(OfficeOneId, "Основной", [CrmStages.Lead]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 5, onShift: true);

        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(2);
        harness.AddShift(ManagerOneId, OfficeOneId, fromUtc, fromUtc.AddHours(8));
        var olderCard = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Lead,
            fromUtc.AddDays(-10));
        var reopenedCard = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Lead,
            fromUtc.AddDays(-9));
        var currentlyClosedWithoutEventInPeriod = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Lead,
            fromUtc.AddHours(1),
            isClosed: true,
            closeReason: CrmCloseReasons.Contract);
        var otherOfficeCard = NewCard(
            OfficeTwoId,
            ManagerOneId,
            CrmStages.Lead,
            fromUtc.AddHours(1));
        harness.Db.CrmCandidateCards.AddRange(
            olderCard,
            reopenedCard,
            currentlyClosedWithoutEventInPeriod,
            otherOfficeCard);
        harness.Db.CrmCandidateHistory.AddRange(
            NewCloseHistory(
                olderCard.Id,
                CrmCloseReasons.NotRelevant,
                fromUtc.AddHours(2),
                ManagerOneId,
                "Анна"),
            NewCloseHistory(
                olderCard.Id,
                CrmCloseReasons.Health,
                fromUtc.AddHours(3),
                ManagerOneId,
                "Анна"),
            NewCloseHistory(
                reopenedCard.Id,
                CrmCloseReasons.Contract,
                fromUtc.AddHours(4),
                ManagerOneId,
                "Анна"),
            NewReopenHistory(
                reopenedCard.Id,
                fromUtc.AddHours(5),
                ManagerOneId,
                "Анна"),
            NewCloseHistory(
                currentlyClosedWithoutEventInPeriod.Id,
                CrmCloseReasons.Contract,
                toUtc,
                ManagerOneId,
                "Анна"),
            NewCloseHistory(
                otherOfficeCard.Id,
                CrmCloseReasons.Woman,
                fromUtc.AddHours(4),
                ManagerOneId,
                "Анна"));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId));

        var data = Assert.IsType<CrmAnalyticsDto>(result.Data);
        Assert.Equal(1, data.Cards.Closed);
        Assert.Equal(3, data.CloseReasons.Sum(x => x.Count));
        Assert.Equal(1, data.CloseReasons.Single(x => x.Reason == CrmCloseReasons.Health).Count);
        Assert.Equal(1, data.CloseReasons.Single(x => x.Reason == CrmCloseReasons.NotRelevant).Count);
        Assert.Equal(1, data.CloseReasons.Single(x => x.Reason == CrmCloseReasons.Contract).Count);
        var periodActivity = Assert.IsType<CrmAnalyticsPeriodActivityDto>(data.PeriodActivity);
        Assert.Equal(3, periodActivity.ClosedCards);
        Assert.Equal(1, periodActivity.ReopenedCards);
        Assert.Equal(0, periodActivity.ClosedCardsOutsideShift);
    }

    [Fact]
    public async Task GetAsync_PeriodActivityIncludesOldCardsAndSurfacesActionsOutsideShift()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(OfficeOneId, "Основной", [CrmStages.Lead, CrmStages.Negotiations]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 5, onShift: false);

        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);
        harness.AddShift(ManagerOneId, OfficeOneId, fromUtc, fromUtc.AddHours(4));
        var oldCard = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Negotiations,
            fromUtc.AddDays(-5),
            isClosed: true,
            closeReason: CrmCloseReasons.Success);
        var outsideShiftCard = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Negotiations,
            fromUtc.AddDays(-4),
            isClosed: true,
            closeReason: CrmCloseReasons.NotRelevant);
        harness.Db.CrmCandidateCards.AddRange(oldCard, outsideShiftCard);
        harness.Db.CrmCandidateHistory.AddRange(
            NewStageHistory(
                oldCard.Id,
                $"{CrmStages.Lead} → {CrmStages.Negotiations}",
                fromUtc.AddHours(1),
                ManagerOneId,
                "Анна"),
            NewCloseHistory(
                oldCard.Id,
                CrmCloseReasons.Success,
                fromUtc.AddHours(2),
                ManagerOneId,
                "Анна"),
            NewStageHistory(
                outsideShiftCard.Id,
                $"{CrmStages.Lead} → {CrmStages.Negotiations}",
                fromUtc.AddHours(5),
                ManagerOneId,
                "Анна"),
            NewCloseHistory(
                outsideShiftCard.Id,
                CrmCloseReasons.NotRelevant,
                fromUtc.AddHours(6),
                ManagerOneId,
                "Анна"));
        await harness.Db.SaveChangesAsync();

        var data = Assert.IsType<CrmAnalyticsDto>((await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId, ManagerOneId))).Data);

        Assert.Equal(0, data.Cards.Received);
        var activity = Assert.IsType<CrmAnalyticsPeriodActivityDto>(data.PeriodActivity);
        Assert.Equal(2, activity.StageChanges);
        Assert.Equal(2, activity.ClosedCards);
        Assert.Equal(1, activity.SuccessfulClosedCards);
        Assert.Equal(1, activity.StageChangesOutsideShift);
        Assert.Equal(1, activity.ClosedCardsOutsideShift);
        var transition = Assert.Single(activity.StageTransitions);
        Assert.Equal(CrmStages.Lead, transition.FromStage);
        Assert.Equal(CrmStages.Negotiations, transition.ToStage);
        Assert.Equal(2, transition.Count);
        Assert.Equal(1, data.CloseReasons.Single(x => x.Reason == CrmCloseReasons.Success).Count);
    }

    [Theory]
    [InlineData(CrmManagerLoadRules.SecondOfficeName, CrmManagerLoadRules.EmptyStage, 1)]
    [InlineData(CrmManagerLoadRules.FourthOfficeName, CrmManagerLoadRules.SubstitutionStage, 1)]
    [InlineData("3 офис", CrmManagerLoadRules.EmptyStage, 2)]
    [InlineData("3 офис", CrmManagerLoadRules.SubstitutionStage, 2)]
    public async Task GetAsync_AppliesServiceStageExclusionOnlyToConfiguredOffice(
        string officeName,
        string serviceStage,
        int expectedActiveLoad)
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(OfficeOneId, officeName, [CrmStages.Lead, serviceStage]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 5, onShift: true);

        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);
        harness.Db.CrmCandidateCards.AddRange(
            NewCard(OfficeOneId, ManagerOneId, CrmStages.Lead, fromUtc.AddMinutes(1)),
            NewCard(OfficeOneId, ManagerOneId, serviceStage, fromUtc.AddMinutes(2)));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId));

        var manager = Assert.Single(Assert.IsType<CrmAnalyticsDto>(result.Data).Managers);
        Assert.Equal(2, manager.CurrentAssignedCards);
        Assert.Equal(expectedActiveLoad, manager.ActiveLoad);
        Assert.Equal(expectedActiveLoad * 20, manager.CapacityUtilizationPercent);
    }

    [Fact]
    public async Task GetAsync_BuildsCallQualityFromAttachedStoredRecordingsInPeriod()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(OfficeOneId, "Основной", [CrmStages.Lead]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 5, onShift: true);
        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(2);
        var analyzedCall = NewRecordedCall(ManagerOneId, fromUtc.AddHours(1), attached: true);
        var waitingCall = NewRecordedCall(ManagerOneId, fromUtc.AddHours(2), attached: true);
        var unattachedCall = NewRecordedCall(ManagerOneId, fromUtc.AddHours(3), attached: false);
        var outsideCall = NewRecordedCall(ManagerOneId, toUtc, attached: true);
        var zeroDurationCall = NewRecordedCall(ManagerOneId, fromUtc.AddHours(4), attached: true);
        zeroDurationCall.DurationSeconds = 0;
        harness.Db.CrmCalls.AddRange(
            analyzedCall,
            waitingCall,
            unattachedCall,
            outsideCall,
            zeroDurationCall);
        var analysis = new CrmCallAiAnalysisDto(
            1,
            8,
            "Следующий шаг зафиксирован.",
            "Связаться",
            "Контакт состоялся",
            "Перезвонить",
            "medium",
            [new CrmCallAiAnalysisPointDto("next_step", "Зафиксирован следующий шаг")],
            [new CrmCallAiAnalysisPointDto("motivation", "Не уточнена мотивация")],
            [],
            [],
            [],
            []);
        harness.Db.CrmCallAiInsights.Add(new CrmCallAiInsightEntity
        {
            CallId = analyzedCall.Id,
            Status = CrmCallAiStatuses.Completed,
            TranscriptText = "Тестовая расшифровка",
            AnalysisJson = System.Text.Json.JsonSerializer.Serialize(
                analysis,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            Score = analysis.Score,
            PromptVersion = "test",
            CreatedAtUtc = fromUtc,
            UpdatedAtUtc = fromUtc,
            TranscribedAtUtc = fromUtc,
            AnalyzedAtUtc = fromUtc
        });
        await harness.Db.SaveChangesAsync();

        var result = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId, ManagerOneId));

        var quality = Assert.IsType<CrmCallQualityAnalyticsDto>(
            Assert.IsType<CrmAnalyticsDto>(result.Data).CallQuality);
        Assert.Equal(2, quality.RecordedCalls);
        Assert.Equal(1, quality.TranscribedCalls);
        Assert.Equal(1, quality.AnalyzedCalls);
        Assert.Equal(50, quality.CoveragePercent);
        Assert.Equal(8, quality.AverageScore);
        Assert.Equal("next_step", Assert.Single(quality.CommonStrengths).Code);
        Assert.Equal("motivation", Assert.Single(quality.CommonWeaknesses).Code);
    }

    [Fact]
    public async Task GetAsync_DecompositionUsesApprovedFirstContactSourcesAndExplicitHistory()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(
            OfficeOneId,
            "Основной",
            [
                CrmStages.Lead,
                CrmStages.Ndz73,
                CrmStages.Ndz26,
                CrmManagerLoadRules.RobotStage,
                CrmStages.Negotiations,
                CrmStages.Questionnaire,
                CrmStages.Ticket,
                CrmStages.PreparingToSend,
                CrmStages.InTransit,
                CrmStages.Signing,
                CrmStages.DealSuccessful
            ]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 20, onShift: true);

        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);
        harness.AddShift(ManagerOneId, OfficeOneId, fromUtc.AddHours(-1), toUtc);
        var negotiationHistoryCard = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Lead,
            fromUtc.AddMinutes(8));
        var officerCard = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Lead,
            fromUtc.AddMinutes(6),
            isClosed: true,
            closeReason: CrmCloseReasons.Officer);
        var notRelevantCard = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Lead,
            fromUtc.AddMinutes(7),
            isClosed: true,
            closeReason: CrmCloseReasons.NotRelevant);
        var questionnaireCard = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Questionnaire,
            fromUtc.AddMinutes(9));
        var ticketCard = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Ticket,
            fromUtc.AddMinutes(10));
        var successfulCard = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Lead,
            fromUtc.AddMinutes(11),
            isClosed: true,
            closeReason: CrmCloseReasons.Success);
        var contractCard = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Lead,
            fromUtc.AddMinutes(12),
            isClosed: true,
            closeReason: CrmCloseReasons.Contract);
        harness.Db.CrmCandidateCards.AddRange(
            NewCard(OfficeOneId, ManagerOneId, CrmStages.Lead, fromUtc.AddMinutes(1)),
            NewCard(OfficeOneId, ManagerOneId, CrmStages.Ndz73, fromUtc.AddMinutes(2)),
            NewCard(
                OfficeOneId,
                ManagerOneId,
                CrmStages.Lead,
                fromUtc.AddMinutes(3),
                isClosed: true,
                closeReason: CrmCloseReasons.NoAnswer),
            NewCard(OfficeOneId, ManagerOneId, CrmManagerLoadRules.RobotStage, fromUtc.AddMinutes(4)),
            NewCard(
                OfficeOneId,
                ManagerOneId,
                CrmStages.Lead,
                fromUtc.AddMinutes(5),
                isClosed: true,
                closeReason: CrmCloseReasons.Disappeared),
            officerCard,
            notRelevantCard,
            negotiationHistoryCard,
            questionnaireCard,
            ticketCard,
            successfulCard,
            contractCard);
        harness.Db.CrmCandidateHistory.AddRange(
            NewStageHistory(
                negotiationHistoryCard.Id,
                $"{CrmStages.Lead} → {CrmStages.Negotiations}",
                fromUtc.AddHours(1),
                ManagerOneId,
                "Анна"),
            NewStageHistory(
                negotiationHistoryCard.Id,
                $"{CrmStages.Negotiations} → {CrmStages.Lead}",
                fromUtc.AddHours(2),
                ManagerOneId,
                "Анна"),
            NewStageHistory(
                questionnaireCard.Id,
                $"{CrmStages.Lead} → {CrmStages.Questionnaire}",
                fromUtc.AddHours(3),
                ManagerOneId,
                "Анна"),
            NewStageHistory(
                ticketCard.Id,
                $"{CrmStages.Lead} → {CrmStages.Ticket}",
                fromUtc.AddHours(4),
                ManagerOneId,
                "Анна"),
            NewCloseHistory(
                successfulCard.Id,
                CrmCloseReasons.Success,
                fromUtc.AddHours(5),
                ManagerOneId,
                "Анна"),
            NewCloseHistory(
                contractCard.Id,
                CrmCloseReasons.Contract,
                fromUtc.AddHours(6),
                ManagerOneId,
                "Анна"),
            NewCloseHistory(
                notRelevantCard.Id,
                CrmCloseReasons.NotRelevant,
                fromUtc.AddHours(7),
                ManagerOneId,
                "Анна"),
            NewCloseHistory(
                officerCard.Id,
                CrmCloseReasons.Officer,
                fromUtc.AddHours(8),
                ManagerOneId,
                "Анна"));
        // Closures must have a source stage; current card state is not historical proof.
        foreach (var history in harness.Db.CrmCandidateHistory.Local.Where(x => x.Action == "Closed"))
            history.StageAtEvent = CrmStages.Lead;
        await harness.Db.SaveChangesAsync();

        var result = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId));

        var decomposition = Assert.IsType<CrmAnalyticsDecompositionDto>(
            Assert.IsType<CrmAnalyticsDto>(result.Data).Decomposition);
        Assert.Equal(12, decomposition.Leads);
        Assert.Equal(6, decomposition.Contacts);
        Assert.Equal(1, decomposition.Questionnaires);
        Assert.Equal(1, decomposition.Tickets);
        Assert.Equal(1, decomposition.Contracts);
        Assert.Equal(50, decomposition.ContactConversionPercent);
        Assert.Equal(16.67, decomposition.QuestionnaireConversionPercent);
        Assert.Equal(100, decomposition.TicketConversionPercent);
        Assert.Equal(100, decomposition.ContractConversionPercent);

        var breakdown = decomposition.ContactBreakdown.ToDictionary(x => x.Label, StringComparer.Ordinal);
        Assert.Equal(6, breakdown["Первый контакт"].Count);
        Assert.Equal(decomposition.Contacts, breakdown.Values.Sum(x => x.Count));
        Assert.DoesNotContain(CrmCloseReasons.NoAnswer, breakdown.Keys);
        Assert.DoesNotContain(CrmCloseReasons.Disappeared, breakdown.Keys);
    }

    [Fact]
    public async Task GetAsync_AggregateDecompositionCountsCohortCardsOnceAcrossManagers()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(
            OfficeOneId,
            "Основной",
            [CrmStages.Lead, CrmStages.Negotiations, CrmStages.Questionnaire]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 10, onShift: true);
        harness.AddManager(ManagerTwoId, OfficeOneId, "Борис", capacity: 10, onShift: true);

        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);
        harness.AddShift(ManagerOneId, OfficeOneId, fromUtc, toUtc);
        harness.AddShift(ManagerTwoId, OfficeOneId, fromUtc, toUtc);
        var firstCard = NewCard(OfficeOneId, ManagerOneId, CrmStages.Questionnaire, fromUtc.AddMinutes(1));
        var secondCard = NewCard(OfficeOneId, ManagerTwoId, CrmStages.Negotiations, fromUtc.AddMinutes(2));
        harness.Db.CrmCandidateCards.AddRange(firstCard, secondCard);
        harness.Db.CrmCandidateHistory.AddRange(
            NewStageHistory(
                firstCard.Id,
                $"{CrmStages.Lead} → {CrmStages.Negotiations}",
                fromUtc.AddHours(1),
                ManagerOneId,
                "Анна"),
            NewStageHistory(
                firstCard.Id,
                $"{CrmStages.Negotiations} → {CrmStages.Questionnaire}",
                fromUtc.AddHours(2),
                ManagerTwoId,
                "Борис"),
            NewStageHistory(
                secondCard.Id,
                $"{CrmStages.Lead} → {CrmStages.Negotiations}",
                fromUtc.AddHours(3),
                ManagerTwoId,
                "Борис"));
        await harness.Db.SaveChangesAsync();

        var all = Assert.IsType<CrmAnalyticsDto>((await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId))).Data);
        var managerOne = Assert.IsType<CrmAnalyticsDto>((await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId, ManagerOneId))).Data);
        var managerTwo = Assert.IsType<CrmAnalyticsDto>((await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId, ManagerTwoId))).Data);

        var allDecomposition = Assert.IsType<CrmAnalyticsDecompositionDto>(all.Decomposition);
        var individual = new[]
        {
            Assert.IsType<CrmAnalyticsDecompositionDto>(managerOne.Decomposition),
            Assert.IsType<CrmAnalyticsDecompositionDto>(managerTwo.Decomposition)
        };
        Assert.Equal(individual.Sum(x => x.Leads), allDecomposition.Leads);
        Assert.Equal(individual.Sum(x => x.Contacts), allDecomposition.Contacts);
        Assert.Equal(2, allDecomposition.Leads);
        Assert.Equal(2, allDecomposition.Contacts);
        Assert.Equal(1, allDecomposition.Questionnaires);
        Assert.All(individual, item => Assert.Equal(1, item.Leads));
        Assert.All(individual, item => Assert.Equal(1, item.Contacts));
        Assert.Equal(1, individual[0].Questionnaires); // The next owner progressed the first owner's group.
        Assert.Equal(0, individual[1].Questionnaires);
    }

    [Fact]
    public async Task GetAsync_DecompositionIgnoresOldCardsAndDeduplicatesWorkBySeveralManagers()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(
            OfficeOneId,
            "Основной",
            [CrmStages.Lead, CrmStages.Negotiations]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 10, onShift: true);
        harness.AddManager(ManagerTwoId, OfficeOneId, "Борис", capacity: 10, onShift: true);

        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);
        harness.AddShift(ManagerOneId, OfficeOneId, fromUtc, toUtc);
        harness.AddShift(ManagerTwoId, OfficeOneId, fromUtc, toUtc);
        var receivedToday = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Negotiations,
            fromUtc.AddMinutes(1));
        var olderCard = NewCard(
            OfficeOneId,
            ManagerOneId,
            CrmStages.Negotiations,
            fromUtc.AddDays(-5));
        harness.Db.CrmCandidateCards.AddRange(receivedToday, olderCard);
        harness.Db.CrmCandidateHistory.AddRange(
            NewStageHistory(
                receivedToday.Id,
                $"{CrmStages.Lead} → {CrmStages.Negotiations}",
                fromUtc.AddHours(1),
                ManagerOneId,
                "Анна"),
            NewStageHistory(
                receivedToday.Id,
                $"{CrmStages.Lead} → {CrmStages.Negotiations}",
                fromUtc.AddHours(2),
                ManagerTwoId,
                "Борис"),
            NewStageHistory(
                olderCard.Id,
                $"{CrmStages.Lead} → {CrmStages.Negotiations}",
                fromUtc.AddHours(3),
                ManagerOneId,
                "Анна"));
        await harness.Db.SaveChangesAsync();

        var data = Assert.IsType<CrmAnalyticsDto>((await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId))).Data);
        var decomposition = Assert.IsType<CrmAnalyticsDecompositionDto>(data.Decomposition);

        Assert.Equal(1, decomposition.Leads);
        Assert.Equal(1, decomposition.Contacts);
        Assert.Equal(100, decomposition.ContactConversionPercent);
    }

    [Fact]
    public async Task GetAsync_FunnelReachedCountsOnlyTransitionsInsideSelectedPeriod()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(
            OfficeOneId,
            "Основной",
            [CrmStages.Lead, CrmStages.Negotiations, CrmStages.Questionnaire]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 10, onShift: true);

        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);
        var card = NewCard(OfficeOneId, ManagerOneId, CrmStages.Questionnaire, fromUtc.AddMinutes(1));
        harness.Db.CrmCandidateCards.Add(card);
        harness.Db.CrmCandidateHistory.AddRange(
            NewStageHistory(
                card.Id,
                $"{CrmStages.Lead} → {CrmStages.Negotiations}",
                fromUtc.AddHours(1),
                ManagerOneId,
                "Анна"),
            NewStageHistory(
                card.Id,
                $"{CrmStages.Negotiations} → {CrmStages.Questionnaire}",
                toUtc.AddHours(1),
                ManagerOneId,
                "Анна"));
        await harness.Db.SaveChangesAsync();

        var data = Assert.IsType<CrmAnalyticsDto>((await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId))).Data);

        var funnel = Assert.Single(data.Funnels);
        Assert.Equal(1, funnel.Stages.Single(x => x.Stage == CrmStages.Negotiations).ReachedCount);
        var questionnaire = funnel.Stages.Single(x => x.Stage == CrmStages.Questionnaire);
        Assert.Equal(0, questionnaire.ReachedCount);
        Assert.Equal(1, questionnaire.CurrentCount);
    }

    [Fact]
    public async Task GetAsync_UsesHalfOpenPeriodAndInitialOwnerForManagerFilter()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(OfficeOneId, "Основной", ["Новый", "Финиш"]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 10, onShift: true);
        harness.AddManager(ManagerTwoId, OfficeOneId, "Борис", capacity: 10, onShift: true);

        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);
        harness.AddShift(ManagerOneId, OfficeOneId, fromUtc.AddHours(-1), toUtc);
        var reassignedCard = NewCard(
            OfficeOneId,
            ManagerTwoId,
            "Финиш",
            fromUtc.AddDays(-1),
            initialManagerUserId: ManagerOneId,
            initialAssignedAtUtc: fromUtc);
        harness.Db.CrmCandidateCards.AddRange(
            reassignedCard,
            NewCard(OfficeOneId, ManagerTwoId, "Новый", fromUtc.AddHours(3)),
            NewCard(OfficeOneId, ManagerOneId, "Новый", toUtc));
        harness.Db.CrmCandidateHistory.AddRange(
            NewStageHistory(
                reassignedCard.Id,
                "Новый → Финиш",
                fromUtc.AddHours(1),
                ManagerOneId,
                "Анна"),
            NewStageHistory(
                reassignedCard.Id,
                "Финиш → Новый",
                fromUtc.AddHours(2),
                ManagerOneId,
                "Анна"));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId, ManagerOneId));

        var data = Assert.IsType<CrmAnalyticsDto>(result.Data);
        Assert.Equal(1, data.Cards.Received);
        Assert.Equal(0, data.Cards.Assigned);
        Assert.Equal(ManagerOneId, data.ManagerUserId);
        Assert.Equal(2, data.ManagerOptions.Count);
        Assert.Single(data.Managers);
        Assert.Equal(ManagerOneId, data.Managers[0].UserId);
        // Current load remains independent of the period and includes the boundary card.
        Assert.Equal(1, data.Managers[0].CurrentAssignedCards);
        Assert.Equal(1, data.Managers[0].CardsInPeriod);
        Assert.Equal(1, data.Managers[0].StageChangedCardsInPeriod);
        Assert.Equal(2, data.Managers[0].StageChangesInPeriod);
    }

    [Fact]
    public async Task GetAsync_AggregateReceiptUsesActualCrmEntryTimeInsteadOfExternalCreationTime()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(OfficeOneId, "Основной", [CrmStages.Lead, CrmStages.Negotiations]);

        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);
        harness.Db.CrmCandidateCards.Add(NewCard(
            OfficeOneId,
            managerUserId: null,
            CrmStages.Lead,
            createdAtUtc: fromUtc.AddMonths(-2),
            enteredCrmAtUtc: fromUtc.AddHours(3)));
        await harness.Db.SaveChangesAsync();

        var data = Assert.IsType<CrmAnalyticsDto>((await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId))).Data);

        Assert.Equal(1, data.Cards.Received);
        Assert.Equal(1, Assert.Single(data.Funnels).Received);
    }

    [Fact]
    public async Task GetAsync_AttributesTransitionsAndClosuresToActualActor()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(OfficeOneId, "Основной", ["Новый", "Финиш"]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 10, onShift: true);
        harness.AddManager(ManagerTwoId, OfficeOneId, "Борис", capacity: 10, onShift: true);

        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);
        harness.AddShift(ManagerOneId, OfficeOneId, fromUtc.AddHours(-1), toUtc);
        harness.AddShift(ManagerTwoId, OfficeOneId, fromUtc.AddHours(-1), toUtc);
        var card = NewCard(
            OfficeOneId,
            ManagerTwoId,
            "Новый",
            fromUtc,
            isClosed: true,
            closeReason: CrmCloseReasons.Success,
            activeLoad: false,
            initialManagerUserId: ManagerOneId,
            initialAssignedAtUtc: fromUtc);
        harness.Db.CrmCandidateCards.Add(card);
        harness.Db.CrmCandidateHistory.AddRange(
            NewStageHistory(
                card.Id,
                CrmActivityDetails.WithComment("Новый → Финиш", "Дозвонился"),
                fromUtc.AddHours(1),
                ManagerTwoId,
                "Борис"),
            NewStageHistory(
                card.Id,
                "Финиш → Новый (воронка обновлена)",
                fromUtc.AddHours(2),
                ManagerTwoId,
                "Борис"),
            NewCloseHistory(
                card.Id,
                CrmCloseReasons.Success,
                fromUtc.AddHours(3),
                ManagerTwoId,
                "Борис"));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId));

        var data = Assert.IsType<CrmAnalyticsDto>(result.Data);
        var firstManager = Assert.Single(data.Managers, x => x.UserId == ManagerOneId);
        var actualActor = Assert.Single(data.Managers, x => x.UserId == ManagerTwoId);
        Assert.Equal(1, firstManager.CardsInPeriod);
        Assert.Equal(0, firstManager.StageChangedCardsInPeriod);
        Assert.Equal(0, firstManager.ClosedCardsInPeriod);
        Assert.Equal(0, actualActor.CardsInPeriod);
        Assert.Equal(1, actualActor.StageChangedCardsInPeriod);
        Assert.Equal(1, actualActor.StageChangesInPeriod);
        Assert.Equal(1, actualActor.ClosedCardsInPeriod);
        Assert.Equal(1, actualActor.SuccessfulClosedCardsInPeriod);

        var funnel = Assert.Single(data.Funnels);
        Assert.Equal(1, Assert.Single(funnel.Stages, x => x.Stage == "Финиш").ReachedCount);
    }

    [Fact]
    public async Task GetAsync_SelectedManagerReceiptAndActionsSurviveMissingShift()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(
            OfficeOneId,
            "Основной",
            [CrmStages.Lead, CrmStages.Negotiations, CrmStages.Questionnaire]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 10, onShift: false);
        harness.AddManager(ManagerTwoId, OfficeOneId, "Борис", capacity: 10, onShift: false);

        var fromUtc = new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);
        harness.AddShift(ManagerTwoId, OfficeOneId, fromUtc, toUtc);

        var card = NewCard(
            OfficeOneId,
            ManagerTwoId,
            CrmStages.Questionnaire,
            fromUtc.AddMinutes(30),
            isClosed: true,
            closeReason: CrmCloseReasons.NotRelevant,
            activeLoad: false,
            initialManagerUserId: ManagerOneId,
            initialAssignedAtUtc: fromUtc.AddMinutes(30));
        harness.Db.CrmCandidateCards.Add(card);
        harness.Db.CrmCandidateHistory.AddRange(
            NewStageHistory(
                card.Id,
                $"{CrmStages.Lead} → {CrmStages.Negotiations}",
                fromUtc.AddHours(1),
                ManagerOneId,
                "Анна"),
            NewCloseHistory(
                card.Id,
                CrmCloseReasons.NotRelevant,
                fromUtc.AddHours(2),
                ManagerOneId,
                "Анна"),
            NewStageHistory(
                card.Id,
                $"{CrmStages.Negotiations} → {CrmStages.Questionnaire}",
                fromUtc.AddHours(3),
                ManagerTwoId,
                "Борис"));
        await harness.Db.SaveChangesAsync();

        var absentResult = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId, ManagerOneId));
        var absent = Assert.IsType<CrmAnalyticsDto>(absentResult.Data);
        var absentDecomposition = Assert.IsType<CrmAnalyticsDecompositionDto>(absent.Decomposition);
        Assert.Equal(1, absent.Cards.Received);
        Assert.Equal(1, absentDecomposition.Leads);
        Assert.Equal(1, absentDecomposition.Contacts);
        Assert.Equal(1, absentDecomposition.Questionnaires);
        var absentManager = Assert.Single(absent.Managers);
        Assert.Equal(1, absentManager.CardsInPeriod);
        Assert.Equal(1, absentManager.StageChangedCardsInPeriod);
        Assert.Equal(1, absentManager.ClosedCardsInPeriod);

        var actualActorResult = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId, ManagerTwoId));
        var actualActor = Assert.IsType<CrmAnalyticsDto>(actualActorResult.Data);
        var actualActorDecomposition = Assert.IsType<CrmAnalyticsDecompositionDto>(actualActor.Decomposition);
        Assert.Equal(0, actualActor.Cards.Received);
        Assert.Equal(0, actualActorDecomposition.Contacts);
        Assert.Equal(0, actualActorDecomposition.Questionnaires);
        Assert.Equal(0, actualActorDecomposition.Tickets);
    }

    [Fact]
    public async Task GetAsync_KeepsClosedCardsFromRemovedStagesInArchiveBucket()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(OfficeOneId, "Основной", ["Новый", "Звонок"]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 10, onShift: true);

        var fromUtc = Now.UtcDateTime.AddDays(-7);
        var toUtc = Now.UtcDateTime.AddDays(1);
        harness.Db.CrmCandidateCards.AddRange(
            NewCard(OfficeOneId, ManagerOneId, "Новый", fromUtc.AddHours(1)),
            NewCard(
                OfficeOneId,
                ManagerOneId,
                "Старый закрывающий этап",
                fromUtc.AddHours(2),
                isClosed: true,
                closeReason: CrmCloseReasons.Success));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId));

        var data = Assert.IsType<CrmAnalyticsDto>(result.Data);
        var funnel = Assert.Single(data.Funnels);
        var archive = Assert.Single(funnel.Stages, x => x.Stage == "Старый закрывающий этап");
        Assert.Equal(1, archive.CurrentCount);
        Assert.Equal(0, archive.ReachedCount); // Current state alone cannot prove a transition.
        Assert.Equal(0, archive.ConversionFromPreviousPercent);
        Assert.Equal(0, archive.ConversionFromReceivedPercent);
        Assert.True(archive.IsArchive);
        Assert.Equal(data.Cards.Received, funnel.Stages.Sum(x => x.CurrentCount));
    }

    [Fact]
    public async Task GetAsync_IncludesFactualFormerAndUnknownManagersWithFallbackNames()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(OfficeOneId, "Основной", ["Новый"]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 10, onShift: true);
        harness.AddFormerManager(FormerManagerId, OfficeOneId, "Вера Бывшая", capacity: 4);

        const string unknownUserId = "legacy-user-without-profile";
        var fromUtc = Now.UtcDateTime.AddDays(-7);
        var toUtc = Now.UtcDateTime.AddDays(1);
        harness.AddShift(FormerManagerId, OfficeOneId, fromUtc, fromUtc.AddHours(8));
        harness.Db.CrmCandidateCards.Add(
            NewCard(OfficeOneId, FormerManagerId, "Новый", fromUtc.AddHours(1), activeLoad: true));
        harness.Db.CrmTasks.Add(
            NewTask(OfficeOneId, unknownUserId, CrmTaskStatuses.Open, Now.UtcDateTime.AddMinutes(-1)));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId));

        var data = Assert.IsType<CrmAnalyticsDto>(result.Data);
        Assert.Contains(data.ManagerOptions, x =>
            x.UserId == FormerManagerId && x.DisplayName == "Вера Бывшая");
        Assert.Contains(data.ManagerOptions, x =>
            x.UserId == unknownUserId && x.DisplayName == unknownUserId);

        var former = Assert.Single(data.Managers, x => x.UserId == FormerManagerId);
        Assert.Equal(4, former.Capacity);
        Assert.Equal(1, former.CurrentAssignedCards);
        Assert.Equal(1, former.ActiveLoad);
        Assert.Equal(1, former.CardsInPeriod);

        var unknown = Assert.Single(data.Managers, x => x.UserId == unknownUserId);
        Assert.Equal(0, unknown.Capacity);
        Assert.Equal(1, unknown.TasksTotal);
        Assert.Equal(1, unknown.OpenTasks);
        Assert.Equal(1, unknown.OverdueTasks);

        var selectedFormerResult = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId, FormerManagerId));
        Assert.Equal(CrmAnalyticsQueryOutcome.Success, selectedFormerResult.Outcome);
        var selectedFormer = Assert.IsType<CrmAnalyticsDto>(selectedFormerResult.Data);
        Assert.Equal(1, selectedFormer.Cards.Received);
        Assert.Single(selectedFormer.Managers);
        Assert.Equal(FormerManagerId, selectedFormer.Managers[0].UserId);
    }

    [Fact]
    public async Task GetAsync_ManagerIsRestrictedToOwnOfficeAndOwnMetrics()
    {
        await using var harness = await Harness.CreateAsync(Now);
        harness.AddOffice(OfficeOneId, "Основной", ["Новый"]);
        harness.AddOffice(OfficeTwoId, "Сибирь", ["Лид"]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 10, onShift: true);
        harness.AddManager(ManagerTwoId, OfficeOneId, "Борис", capacity: 10, onShift: true);
        var fromUtc = Now.UtcDateTime.AddDays(-7);
        var toUtc = Now.UtcDateTime.AddDays(1);
        harness.AddShift(ManagerOneId, OfficeOneId, fromUtc, fromUtc.AddHours(8));
        harness.AddShift(ManagerTwoId, OfficeOneId, fromUtc, fromUtc.AddHours(8));
        harness.Db.CrmCandidateCards.AddRange(
            NewCard(OfficeOneId, ManagerOneId, "Новый", fromUtc.AddHours(1)),
            NewCard(OfficeOneId, ManagerTwoId, "Новый", fromUtc.AddHours(2)));
        await harness.Db.SaveChangesAsync();

        var ownResult = await harness.Sut.GetAsync(
            OfficeScope.ForOffice(OfficeOneId),
            ManagerOneId,
            isAdmin: false,
            new CrmAnalyticsQuery(fromUtc, toUtc));
        var ownData = Assert.IsType<CrmAnalyticsDto>(ownResult.Data);
        Assert.Equal(OfficeOneId, ownData.OfficeId);
        Assert.Equal(ManagerOneId, ownData.ManagerUserId);
        Assert.Equal(1, ownData.Cards.Received);
        Assert.Single(ownData.ManagerOptions);
        Assert.Equal(ManagerOneId, ownData.ManagerOptions[0].UserId);

        var foreignOfficeResult = await harness.Sut.GetAsync(
            OfficeScope.ForOffice(OfficeOneId),
            ManagerOneId,
            isAdmin: false,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeTwoId));
        Assert.Equal(CrmAnalyticsQueryOutcome.Forbidden, foreignOfficeResult.Outcome);

        var foreignManagerResult = await harness.Sut.GetAsync(
            OfficeScope.ForOffice(OfficeOneId),
            ManagerOneId,
            isAdmin: false,
            new CrmAnalyticsQuery(fromUtc, toUtc, OfficeOneId, ManagerTwoId));
        Assert.Equal(CrmAnalyticsQueryOutcome.Forbidden, foreignManagerResult.Outcome);
    }

    [Fact]
    public async Task GetAsync_RejectsInvalidOrExcessivePeriodBeforeQuerying()
    {
        await using var harness = await Harness.CreateAsync(Now);
        var fromUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var reversed = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, fromUtc));
        Assert.Equal(CrmAnalyticsQueryOutcome.BadRequest, reversed.Outcome);

        var exactLimit = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, fromUtc.AddDays(366)));
        Assert.Equal(CrmAnalyticsQueryOutcome.Success, exactLimit.Outcome);

        var tooLong = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(fromUtc, fromUtc.AddDays(366).AddTicks(1)));
        Assert.Equal(CrmAnalyticsQueryOutcome.BadRequest, tooLong.Outcome);
    }

    [Fact]
    public async Task GetAsync_ExecutesOfficeSortWithRelationalProvider()
    {
        await using var harness = await Harness.CreateSqliteAsync(Now);
        harness.AddOffice(OfficeOneId, "Ярославль", ["Новый"]);
        harness.AddManager(ManagerOneId, OfficeOneId, "Анна", capacity: 10, onShift: true);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Sut.GetAsync(
            OfficeScope.GlobalAdmin,
            "admin",
            isAdmin: true,
            new CrmAnalyticsQuery(Now.UtcDateTime.AddDays(-1), Now.UtcDateTime.AddDays(1), OfficeOneId));

        Assert.Equal(CrmAnalyticsQueryOutcome.Success, result.Outcome);
    }

[Fact]
    public async Task GetAsync_EventsAreAdditiveAndSurviveCrossOfficeMove()
    {
        await using var h = await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddOffice(OfficeTwoId, "Второй", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 10, false);
        h.AddManager(ManagerTwoId, OfficeTwoId, "Борис", 10, false);
        var from = Now.UtcDateTime.AddDays(-3);
        var card = NewCard(OfficeOneId, ManagerOneId, CrmStages.Lead, from.AddDays(-20));
        card.EntryOfficeId = OfficeOneId;
        card.InitialAssignedOfficeId = OfficeOneId;
        h.Db.CrmCandidateCards.Add(card);
        await h.Db.SaveChangesAsync();
        card.IsClosed = true;
        card.CloseReason = CrmCloseReasons.Success;
        var closed = NewCloseHistory(card.Id, CrmCloseReasons.Success, from.AddHours(1), ManagerOneId, "Анна");
        h.Db.AddCrmHistory(closed);
        await h.Db.SaveChangesAsync();
        card.IsClosed = false;
        card.CloseReason = null;
        var reopened = NewReopenHistory(card.Id, from.AddDays(1).AddHours(1), ManagerOneId, "Анна");
        h.Db.AddCrmHistory(reopened);
        await h.Db.SaveChangesAsync();
        Assert.Equal(CrmCloseReasons.Success, reopened.PreviousCloseReason);
        card.OfficeId = OfficeTwoId;
        card.ManagerUserId = ManagerTwoId;
        await h.Db.SaveChangesAsync();

        async Task<CrmAnalyticsPeriodActivityDto> Read(DateTime a, DateTime b, Guid office) =>
            (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true,
                new CrmAnalyticsQuery(a, b, office))).Data!.PeriodActivity!;
        var d1 = await Read(from, from.AddDays(1), OfficeOneId);
        var d2 = await Read(from.AddDays(1), from.AddDays(2), OfficeOneId);
        var both = await Read(from, from.AddDays(2), OfficeOneId);
        Assert.Equal(1, d1.SuccessfulClosedCards);
        Assert.Equal(1, d2.ReopenedCards);
        Assert.Equal(1, d2.ReturnedFromSuccess);
        Assert.Equal(d1.SuccessfulClosedCards + d2.SuccessfulClosedCards, both.SuccessfulClosedCards);
        Assert.Equal(1, both.ClosedCards);
        Assert.Equal(0, (await Read(from, from.AddDays(2), OfficeTwoId)).ClosedCards);
        Assert.Equal(OfficeOneId, closed.OfficeId);
        Assert.Equal(ManagerOneId, closed.ResponsibleUserId);
        Assert.False(closed.ContextInferred);
    }

    [Fact]
    public async Task GetAsync_AssignmentsFollowRecipientsNotActorAndUnknownIsNotInvented()
    {
        await using var h = await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 10, false);
        h.AddManager(ManagerTwoId, OfficeOneId, "Борис", 10, false);
        var from = Now.UtcDateTime.AddDays(-1);
        var card = NewCard(OfficeOneId, ManagerOneId, CrmStages.Lead, from.AddDays(-10));
        h.Db.CrmCandidateCards.Add(card);
        await h.Db.SaveChangesAsync();
        card.ManagerUserId = ManagerTwoId;
        var transfer = new CrmCandidateHistoryEntity {
            Id = Guid.NewGuid(), CardId = card.Id, Action = "Assigned",
            TargetUserId = ManagerTwoId, ActorUserId = "admin", ActorName = "Администратор", CreatedAtUtc = from.AddHours(1)
        };
        h.Db.AddCrmHistory(transfer);
        h.Db.CrmCandidateHistory.Add(new CrmCandidateHistoryEntity {
            Id = Guid.NewGuid(), CardId = card.Id, Action = "Assigned", ActorUserId = "admin",
            ActorName = "Администратор", CreatedAtUtc = from.AddHours(2)
        });
        await h.Db.SaveChangesAsync();
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true,
            new CrmAnalyticsQuery(from, from.AddDays(1), OfficeOneId))).Data!;
        Assert.Equal(ManagerOneId, transfer.PreviousUserId);
        Assert.Equal(ManagerTwoId, transfer.ResponsibleUserId);
        Assert.Equal(1, data.PeriodActivity!.Assignments);
        Assert.Equal(1, data.PeriodActivity.TransfersReceived);
        Assert.Equal(1, data.PeriodActivity.UnattributedAssignments);
        Assert.Equal(1, data.Managers.Single(x => x.UserId == ManagerOneId).TransfersSent);
        Assert.Equal(1, data.Managers.Single(x => x.UserId == ManagerTwoId).TransfersReceived);
        Assert.All(data.Managers, x => Assert.Equal(0, x.CardsInPeriod));
    }

    [Fact]
    public async Task GetAsync_SuccessStageDoesNotMeanClosureAndTicketDoesNotInventQuestionnaire()
    {
        await using var h = await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        var from = Now.UtcDateTime.AddDays(-1);
        var stageOnly = NewCard(OfficeOneId, null, CrmStages.DealSuccessful, from);
        var ticketOnly = NewCard(OfficeOneId, null, CrmStages.Ticket, from);
        h.Db.CrmCandidateCards.AddRange(stageOnly, ticketOnly);
        h.Db.CrmCandidateHistory.AddRange(
            NewStageHistory(stageOnly.Id, $"{CrmStages.Lead} → {CrmStages.DealSuccessful}", from.AddHours(1)),
            NewStageHistory(ticketOnly.Id, $"{CrmStages.Lead} → {CrmStages.Ticket}", from.AddHours(1)),
            NewStageHistory(ticketOnly.Id, $"{CrmStages.Questionnaire} → {CrmStages.Questionnaire}", from.AddHours(2)));
        await h.Db.SaveChangesAsync();
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true,
            new CrmAnalyticsQuery(from, from.AddDays(1), OfficeOneId))).Data!;
        Assert.Equal(0, data.Cards.SuccessfulClosed);
        Assert.Equal(0, data.Decomposition!.Contracts);
        Assert.Equal(1, data.Decomposition.Tickets);
        Assert.Equal(0, data.Decomposition.Questionnaires);
        Assert.Equal(0, data.Decomposition.TicketConversionPercent);
        Assert.Null(data.Sales!.Cohort.Results.Single(x => x.Key == "sales.cohort.tickets").Percent);
        Assert.Equal(2, data.PeriodActivity!.StageChanges);
        Assert.Equal(0, data.Funnels.Single().Stages.Single(x => x.Stage == CrmStages.Ndz73).ReachedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetEvidenceAsync_PaginatesExactRowsAndKeepsCurrentCardScope(bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddOffice(OfficeTwoId, "Второй", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 10, false);
        h.AddManager(ManagerTwoId, OfficeOneId, "Борис", 10, false);
        var from = Now.UtcDateTime.AddDays(-1);
        for (var n = 0; n < 52; n++)
        {
            var card = NewCard(OfficeOneId, ManagerOneId, CrmStages.Lead, from.AddMinutes(n));
            card.EntryOfficeId = OfficeOneId;
            card.InitialAssignedOfficeId = OfficeOneId;
            var person = new CandidatePersonEntity { Id = Guid.NewGuid(), FullName = "Кандидат " + n, OfficeId = OfficeOneId };
            card.Response = new CandidateResponseEntity {
                Id = card.ResponseId, Person = person, PersonId = person.Id, FullName = person.FullName,
                SourceResponseId = Guid.NewGuid().ToString(), CardFingerprint = Guid.NewGuid().ToString()
            };
            h.Db.CrmCandidateCards.Add(card);
            var ev = NewCloseHistory(card.Id, CrmCloseReasons.Success, from.AddHours(2).AddMinutes(n), ManagerOneId, "Анна");
            ev.OfficeId = OfficeOneId;
            ev.ResponsibleUserId = ManagerOneId;
            h.Db.CrmCandidateHistory.Add(ev);
            if (n == 51) { card.OfficeId = OfficeTwoId; card.ManagerUserId = ManagerTwoId; }
        }
        await h.Db.SaveChangesAsync();
        var query = new CrmAnalyticsQuery(from, from.AddDays(1), OfficeOneId, ManagerOneId);
        var first = await h.Sut.GetEvidenceAsync(OfficeScope.ForOffice(OfficeOneId), ManagerOneId, false, query, "activity.success", 1);
        Assert.Equal(CrmAnalyticsQueryOutcome.Success, first.Outcome);
        Assert.Equal(52, first.Data!.Total);
        Assert.Equal(1, first.Data.RestrictedCount);
        Assert.Equal(50, first.Data.Rows.Count);
        Assert.DoesNotContain(first.Data.Rows, x => x.CandidateName == "Кандидат 51");
        var second = (await h.Sut.GetEvidenceAsync(OfficeScope.ForOffice(OfficeOneId), ManagerOneId, false, query, "activity.success", 2)).Data!;
        Assert.Single(second.Rows);
        Assert.DoesNotContain(second.Rows[0].CardId, first.Data.Rows.Select(x => x.CardId));
        var global = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, query, "activity.success", 2)).Data!;
        Assert.Equal(0, global.RestrictedCount);
        Assert.Equal(2, global.Rows.Count);
        var cohort = (await h.Sut.GetEvidenceAsync(OfficeScope.ForOffice(OfficeOneId), ManagerOneId, false, query, "cohort.received", 1)).Data!;
        Assert.Equal(52, cohort.Total);
        Assert.Equal(1, cohort.RestrictedCount);
        Assert.Equal(CrmAnalyticsQueryOutcome.Forbidden,
            (await h.Sut.GetEvidenceAsync(OfficeScope.NoAccess, "admin", true, query, "activity.success", 1)).Outcome);
        Assert.Equal(CrmAnalyticsQueryOutcome.Forbidden,
            (await h.Sut.GetEvidenceAsync(OfficeScope.ForOffice(OfficeTwoId), ManagerOneId, false, query, "activity.success", 1)).Outcome);
    }

    [Fact]
    public void AnalyticsHistoryMigrationMatchesNpgsqlModel()
    {
        using var db = new OrbitaDbContext(new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=not_opened;Username=not_used;Password=not_used").Options);
        // Metadata comparison only, without opening a connection.
        Assert.False(db.Database.HasPendingModelChanges());
    }


    [Fact]
    public async Task GetAsync_AllOfficesKeepIndependentFunnelsEvenWithIdenticalStageNames()
    {
        await using var h = await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", [CrmStages.Lead, CrmStages.Negotiations]);
        h.AddOffice(OfficeTwoId, "Второй", [CrmStages.Lead, CrmStages.Negotiations]);
        var from = Now.UtcDateTime.AddDays(-1);
        var first = NewCard(OfficeOneId, null, CrmStages.Negotiations, from);
        var second = NewCard(OfficeTwoId, null, CrmStages.Lead, from);
        h.Db.CrmCandidateCards.AddRange(first, second);
        h.Db.CrmCandidateHistory.Add(NewStageHistory(first.Id,
            $"{CrmStages.Lead} → {CrmStages.Negotiations}", from.AddHours(1)));
        await h.Db.SaveChangesAsync();
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true,
            new CrmAnalyticsQuery(from, from.AddDays(1)))).Data!;
        Assert.Equal(1, data.Funnels.Single(x => x.OfficeId == OfficeOneId).Stages.Single(x => x.Stage == CrmStages.Negotiations).ReachedCount);
        Assert.Equal(0, data.Funnels.Single(x => x.OfficeId == OfficeTwoId).Stages.Single(x => x.Stage == CrmStages.Negotiations).ReachedCount);
        Assert.All(data.Funnels.SelectMany(x => x.Stages), x => Assert.InRange(x.ConversionFromReceivedPercent, 0, 100));
        var empty = await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true,
            new CrmAnalyticsQuery(from, from.AddDays(1)), "stage:" + OfficeTwoId + ":" + CrmStages.Negotiations, 1);
        Assert.Equal(0, empty.Data!.Total);
    }
    private static CrmCandidateCardEntity NewCard(
        Guid officeId,
        string? managerUserId,
        string stage,
        DateTime createdAtUtc,
        bool isClosed = false,
        string? closeReason = null,
        bool activeLoad = true,
        string? initialManagerUserId = null,
        DateTime? initialAssignedAtUtc = null,
        DateTime? enteredCrmAtUtc = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ResponseId = Guid.NewGuid(),
            OfficeId = officeId,
            ManagerUserId = managerUserId,
            InitialManagerUserId = initialManagerUserId ?? managerUserId,
            InitialAssignedAtUtc = initialManagerUserId is not null || managerUserId is not null
                ? initialAssignedAtUtc ?? createdAtUtc
                : null,
            Stage = stage,
            EnteredCrmAtUtc = enteredCrmAtUtc ?? createdAtUtc,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
            StageChangedAtUtc = createdAtUtc,
            IsClosed = isClosed,
            CloseReason = closeReason,
            ClosedAtUtc = isClosed ? createdAtUtc.AddHours(1) : null,
            IsInActiveLoad = activeLoad && !isClosed
        };

    private static CrmCandidateHistoryEntity NewStageHistory(
        Guid cardId,
        string details,
        DateTime createdAtUtc,
        string actorUserId = "admin",
        string actorName = "Администратор") =>
        new()
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            Action = "StageChanged",
            Details = details,
            ActorUserId = actorUserId,
            ActorName = actorName,
            CreatedAtUtc = createdAtUtc
        };

    private static CrmCandidateHistoryEntity NewCloseHistory(
        Guid cardId,
        string reason,
        DateTime createdAtUtc,
        string actorUserId,
        string actorName) =>
        new()
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            Action = "Closed",
            Details = CrmActivityDetails.WithComment(reason, "Комментарий к закрытию"),
            ActorUserId = actorUserId,
            ActorName = actorName,
            CreatedAtUtc = createdAtUtc
        };

    private static CrmCandidateHistoryEntity NewReopenHistory(
        Guid cardId,
        DateTime createdAtUtc,
        string actorUserId,
        string actorName) =>
        new()
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            Action = "Reopened",
            ActorUserId = actorUserId,
            ActorName = actorName,
            CreatedAtUtc = createdAtUtc
        };

    private static CrmTaskEntity NewTask(Guid officeId, string assigneeUserId, string status, DateTime? dueAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            Title = "Тестовая задача",
            AssigneeUserId = assigneeUserId,
            CreatorUserId = "admin",
            CreatorName = "Администратор",
            Status = status,
            DueAtUtc = dueAtUtc,
            CreatedAtUtc = Now.UtcDateTime.AddDays(-2),
            CompletedAtUtc = status == CrmTaskStatuses.Open ? null : Now.UtcDateTime.AddDays(-1)
        };

    private static CrmCallEntity NewRecordedCall(string managerUserId, DateTime startedAtUtc, bool attached) =>
        new()
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeOneId,
            CardId = attached ? Guid.NewGuid() : null,
            Provider = CrmTelephonyProviders.Asterisk,
            ExternalCallId = Guid.NewGuid().ToString("N"),
            Direction = CrmCallDirections.Outgoing,
            CallerPhone = "79000000001",
            CalledPhone = "79000000002",
            ClientPhoneNormalized = "79000000002",
            ManagerUserId = managerUserId,
            StartedAtUtc = startedAtUtc,
            DurationSeconds = 60,
            RecordingStoragePath = $"{Guid.NewGuid():N}.bin",
            RecordingContentType = "audio/mpeg",
            RecordingFileName = "call.mp3",
            ReceivedAtUtc = startedAtUtc,
            UpdatedAtUtc = startedAtUtc
        };

    private sealed class Harness : IAsyncDisposable
    {
        private const string ManagerRoleId = "analytics-manager-role";
        private readonly SqliteConnection? sqliteConnection;

        private Harness(
            OrbitaDbContext db,
            CrmAnalyticsQueryService sut,
            SqliteConnection? sqliteConnection = null)
        {
            Db = db;
            Sut = sut;
            this.sqliteConnection = sqliteConnection;
        }

        public OrbitaDbContext Db { get; }
        public CrmAnalyticsQueryService Sut { get; }

        public static async Task<Harness> CreateAsync(
            DateTimeOffset now,
            IReadOnlyDictionary<Guid, string[]>? alternateEntryStages = null)
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new OrbitaDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.Roles.Add(new IdentityRole
            {
                Id = ManagerRoleId,
                Name = PanelRoles.Manager,
                NormalizedName = PanelRoles.Manager.ToUpperInvariant()
            });
            await db.SaveChangesAsync();
            return new Harness(
                db,
                new CrmAnalyticsQueryService(
                    db,
                    new FixedTimeProvider(now),
                    CreateAnalyticsOptions(alternateEntryStages)));
        }

        public static async Task<Harness> CreateSqliteAsync(DateTimeOffset now)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new OrbitaDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.Roles.Add(new IdentityRole
            {
                Id = ManagerRoleId,
                Name = PanelRoles.Manager,
                NormalizedName = PanelRoles.Manager.ToUpperInvariant()
            });
            await db.SaveChangesAsync();
            return new Harness(
                db,
                new CrmAnalyticsQueryService(
                    db,
                    new FixedTimeProvider(now),
                    CreateAnalyticsOptions()),
                connection);
        }

        private static IOptions<CrmAnalyticsOptions> CreateAnalyticsOptions(
            IReadOnlyDictionary<Guid, string[]>? alternateEntryStages = null) =>
            Options.Create(new CrmAnalyticsOptions
            {
                AlternateEntryStagesByOffice = alternateEntryStages?.ToDictionary(
                    item => item.Key.ToString("D"),
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            });

        public void AddOffice(Guid id, string name, IReadOnlyList<string> stages)
        {
            Db.Offices.Add(new OfficeEntity
            {
                Id = id,
                Name = name,
                RegistrationSecretHash = "test",
                CreatedAtUtc = Now.UtcDateTime.AddYears(-1),
                IsEnabled = true,
                CrmEnabled = true,
                CrmStagesJson = CrmStages.Serialize(stages)
            });
        }

        public void AddManager(string userId, Guid officeId, string fullName, int capacity, bool onShift)
        {
            AddProfiledUser(userId, officeId, fullName, capacity, onShift);
            Db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = ManagerRoleId });
        }

        public void AddFormerManager(string userId, Guid officeId, string fullName, int capacity)
        {
            AddProfiledUser(userId, officeId, fullName, capacity, onShift: false);
        }

        public void AddShift(
            string userId,
            Guid officeId,
            DateTime startedAtUtc,
            DateTime endedAtUtc)
        {
            Db.CrmManagerShifts.Add(new CrmManagerShiftEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = officeId,
                ManagerUserId = userId,
                StartedAtUtc = DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc),
                EndedAtUtc = DateTime.SpecifyKind(endedAtUtc, DateTimeKind.Utc),
                EndReason = CrmShiftEndReasons.Manual,
                EndedByUserId = userId
            });
        }

        private void AddProfiledUser(
            string userId,
            Guid officeId,
            string fullName,
            int capacity,
            bool onShift)
        {
            Db.Users.Add(new IdentityUser
            {
                Id = userId,
                UserName = $"{userId}@test.local",
                NormalizedUserName = $"{userId}@test.local".ToUpperInvariant(),
                Email = $"{userId}@test.local",
                NormalizedEmail = $"{userId}@test.local".ToUpperInvariant()
            });
            Db.PanelUserProfiles.Add(new PanelUserProfileEntity
            {
                UserId = userId,
                OfficeId = officeId,
                FullName = fullName,
                CrmCapacity = capacity,
                CrmShiftActive = onShift,
                CrmShiftStartedAtUtc = onShift ? DateTime.UtcNow : null
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (sqliteConnection is not null)
            {
                await sqliteConnection.DisposeAsync();
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

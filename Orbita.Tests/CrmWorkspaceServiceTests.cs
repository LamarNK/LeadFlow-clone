using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmWorkspaceServiceTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task CreateCard_WhenCrmDisabled_DoesNothing()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: false);
        var response = await SeedResponseAsync(harness.Db);

        await harness.Sut.CreateCardForResponseAsync(response);

        Assert.Empty(harness.Db.CrmCandidateCards);
    }

    [Fact]
    public async Task CreateCard_WhenEnabled_CreatesCardWithoutTouchingBitrixFields()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var response = await SeedResponseAsync(harness.Db);
        response.Status = ResponseStatuses.ActionRequired;
        response.BitrixEntityId = "keep-me";
        await harness.Db.SaveChangesAsync();

        await harness.Sut.CreateCardForResponseAsync(response);

        var card = Assert.Single(harness.Db.CrmCandidateCards);
        Assert.Equal(response.Id, card.ResponseId);
        Assert.Equal(CrmStages.Lead, card.Stage);
        Assert.Null(card.ManagerUserId);
        Assert.Equal(ResponseStatuses.ActionRequired, response.Status);
        Assert.Equal("keep-me", response.BitrixEntityId);
    }

    [Fact]
    public async Task CreateCard_AutoAssignsToManagerOnShiftWithCapacity()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("mgr@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db);

        await harness.Sut.CreateCardForResponseAsync(response);

        var card = Assert.Single(harness.Db.CrmCandidateCards);
        Assert.Equal(manager.Id, card.ManagerUserId);
        Assert.True(card.IsInActiveLoad);
    }

    [Fact]
    public async Task StartShift_FillsFreeSlotsFromUnassignedQueue()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("shift@test.local", capacity: 2, onShift: false);
        var r1 = await SeedResponseAsync(harness.Db, "src-1");
        var r2 = await SeedResponseAsync(harness.Db, "src-2");
        var r3 = await SeedResponseAsync(harness.Db, "src-3");
        harness.Db.CrmCandidateCards.AddRange(NewCard(r1.Id), NewCard(r2.Id), NewCard(r3.Id));
        await harness.Db.SaveChangesAsync();

        var ok = await harness.Sut.StartShiftAsync(OfficeId, manager.Id);
        Assert.True(ok);

        Assert.Equal(2, await harness.Db.CrmCandidateCards.CountAsync(x => x.ManagerUserId == manager.Id));
        Assert.Equal(1, await harness.Db.CrmCandidateCards.CountAsync(x => x.ManagerUserId == null));
        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == manager.Id);
        Assert.True(profile.CrmShiftActive);
        Assert.NotNull(profile.CrmShiftStartedAtUtc);

        var history = Assert.Single(await harness.Db.CrmManagerShifts.Where(x => x.ManagerUserId == manager.Id).ToListAsync());
        Assert.Equal(OfficeId, history.OfficeId);
        Assert.Null(history.EndedAtUtc);
        Assert.Null(history.EndReason);
        Assert.Equal(profile.CrmShiftStartedAtUtc, history.StartedAtUtc);
    }

    [Fact]
    public async Task StopShift_ClearsStartedAt_AndClosesHistory()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("stop@test.local", capacity: 2, onShift: false);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, manager.Id));

        var ok = await harness.Sut.StopShiftAsync(OfficeId, manager.Id);
        Assert.True(ok);

        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == manager.Id);
        Assert.False(profile.CrmShiftActive);
        Assert.Null(profile.CrmShiftStartedAtUtc);

        var history = Assert.Single(await harness.Db.CrmManagerShifts.Where(x => x.ManagerUserId == manager.Id).ToListAsync());
        Assert.NotNull(history.EndedAtUtc);
        Assert.Equal(CrmShiftEndReasons.Manual, history.EndReason);
        Assert.Equal(manager.Id, history.EndedByUserId);
        Assert.True(history.EndedAtUtc >= history.StartedAtUtc);
    }

    [Fact]
    public async Task ExpireStaleShifts_StopsShiftOlderThanMaxDuration()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("stale@test.local", capacity: 2, onShift: false);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, manager.Id));
        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == manager.Id);
        var started = DateTime.UtcNow - CrmShiftRules.MaxDuration - TimeSpan.FromMinutes(1);
        profile.CrmShiftStartedAtUtc = started;
        var open = await harness.Db.CrmManagerShifts.SingleAsync(x => x.ManagerUserId == manager.Id && x.EndedAtUtc == null);
        open.StartedAtUtc = started;
        await harness.Db.SaveChangesAsync();

        var expired = await harness.Sut.ExpireStaleShiftsAsync();
        Assert.Equal(1, expired);

        await harness.Db.Entry(profile).ReloadAsync();
        Assert.False(profile.CrmShiftActive);
        Assert.Null(profile.CrmShiftStartedAtUtc);

        await harness.Db.Entry(open).ReloadAsync();
        Assert.NotNull(open.EndedAtUtc);
        Assert.Equal(CrmShiftEndReasons.AutoMaxDuration, open.EndReason);
        Assert.Null(open.EndedByUserId);
    }

    [Fact]
    public async Task ExpireStaleShifts_StopsLegacyActiveWithoutStartTime()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("legacy@test.local", capacity: 2, onShift: true);
        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == manager.Id);
        profile.CrmShiftStartedAtUtc = null;
        await harness.Db.SaveChangesAsync();

        var expired = await harness.Sut.ExpireStaleShiftsAsync();
        Assert.Equal(1, expired);
        Assert.False(profile.CrmShiftActive);

        var history = Assert.Single(await harness.Db.CrmManagerShifts.Where(x => x.ManagerUserId == manager.Id).ToListAsync());
        Assert.NotNull(history.EndedAtUtc);
        Assert.Equal(CrmShiftEndReasons.LegacyCleanup, history.EndReason);
    }

    [Fact]
    public async Task StartShift_WhenAlreadyOpen_SupersedesPreviousHistory()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("restart@test.local", capacity: 2, onShift: false);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, manager.Id));
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, manager.Id));

        var rows = await harness.Db.CrmManagerShifts
            .Where(x => x.ManagerUserId == manager.Id)
            .OrderBy(x => x.StartedAtUtc)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(CrmShiftEndReasons.Superseded, rows[0].EndReason);
        Assert.NotNull(rows[0].EndedAtUtc);
        Assert.Null(rows[1].EndedAtUtc);
    }

    [Fact]
    public async Task GetBoard_Managers_ExposeShiftTiming()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var onShift = await harness.CreateManagerAsync("onshift-board@test.local", capacity: 5, onShift: false);
        var offShift = await harness.CreateManagerAsync("offshift-board@test.local", capacity: 5, onShift: false);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, onShift.Id));
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, offShift.Id));
        Assert.True(await harness.Sut.StopShiftAsync(OfficeId, offShift.Id));

        var board = await harness.Sut.GetBoardAsync(OfficeId, onShift.Id, isAdmin: true);
        Assert.NotNull(board);

        var active = Assert.Single(board!.Managers, x => x.UserId == onShift.Id);
        Assert.True(active.IsShiftActive);
        Assert.NotNull(active.ShiftStartedAtUtc);
        Assert.Null(active.LastShiftEndedAtUtc);

        var inactive = Assert.Single(board.Managers, x => x.UserId == offShift.Id);
        Assert.False(inactive.IsShiftActive);
        Assert.Null(inactive.ShiftStartedAtUtc);
        Assert.NotNull(inactive.LastShiftEndedAtUtc);
    }

    [Fact]
    public async Task CreateCard_DoesNotAssignToStaleShift()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("stale-assign@test.local", capacity: 5, onShift: true);
        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == manager.Id);
        profile.CrmShiftStartedAtUtc = DateTime.UtcNow - CrmShiftRules.MaxDuration - TimeSpan.FromHours(1);
        await harness.Db.SaveChangesAsync();
        var response = await SeedResponseAsync(harness.Db);

        await harness.Sut.CreateCardForResponseAsync(response);

        var card = Assert.Single(harness.Db.CrmCandidateCards);
        Assert.Null(card.ManagerUserId);
    }

    [Fact]
    public async Task Close_RemovesFromActiveLoad()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("close@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (ok, error) = await harness.Sut.CloseAsync(card.Id, CrmCloseReasons.Refused, "не интересно", manager.Id, isAdmin: false);
        Assert.True(ok, error);
        Assert.True(card.IsClosed);
        Assert.False(card.IsInActiveLoad);
        Assert.Equal(CrmCloseReasons.Refused, card.CloseReason);
    }

    [Fact]
    public async Task SetOfficeFunnel_SavesCustomStagesAndMovesOrphans()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("funnel@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, manager.Id);
        card.Stage = CrmStages.Ticket;
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (ok, error) = await harness.Sut.SetOfficeFunnelAsync(
            OfficeId,
            ["Новый", "В работе", "Готово"],
            manager.Id);
        Assert.True(ok, error);

        var office = await harness.Db.Offices.SingleAsync(x => x.Id == OfficeId);
        Assert.Equal(["Новый", "В работе", "Готово"], CrmStages.Resolve(office.CrmStagesJson));
        Assert.Equal("Новый", card.Stage);

        var board = await harness.Sut.GetBoardAsync(OfficeId, manager.Id, isAdmin: true);
        Assert.NotNull(board);
        Assert.Equal(["Новый", "В работе", "Готово"], board.FunnelStages);
        Assert.Equal(["Новый", "В работе", "Готово"], board.Stages.Select(s => s.Name).ToList());
    }

    [Fact]
    public async Task SetOfficeSettings_EnablesDeadlineNotificationsAndDismissesThemOnDisable()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("deadline@test.local", capacity: 5, onShift: true);

        Assert.True(await harness.Sut.SetOfficeSettingsAsync(
            OfficeId,
            enabled: true,
            requireStageComment: false,
            deadlineNotificationsEnabled: true));
        var office = await harness.Db.Offices.SingleAsync(x => x.Id == OfficeId);
        Assert.True(office.CrmDeadlineNotificationsEnabled);
        Assert.NotNull(office.CrmDeadlineNotificationsEnabledAtUtc);

        var task = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(null, "Проверить документы", null, manager.Id, DateTime.UtcNow.AddHours(2)),
            manager.Id,
            isAdmin: false);
        Assert.NotNull(task);
        var taskEntity = await harness.Db.CrmTasks.SingleAsync(x => x.Id == task.Id);
        var notification = new CrmTaskNotificationEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            TaskId = task.Id,
            ReminderVersion = taskEntity.ReminderVersion,
            RecipientUserId = manager.Id,
            Kind = CrmTaskNotificationKinds.DueIn24Hours,
            DueAtUtc = taskEntity.DueAtUtc!.Value,
            CreatedAtUtc = DateTime.UtcNow
        };
        harness.Db.CrmTaskNotifications.Add(notification);
        await harness.Db.SaveChangesAsync();

        Assert.True(await harness.Sut.SetOfficeSettingsAsync(
            OfficeId,
            enabled: true,
            requireStageComment: false,
            deadlineNotificationsEnabled: false));
        Assert.False(office.CrmDeadlineNotificationsEnabled);
        Assert.Null(office.CrmDeadlineNotificationsEnabledAtUtc);
        Assert.NotNull(notification.DismissedAtUtc);
    }

    [Fact]
    public async Task CreateCard_UsesFirstOfficeStage()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var office = await harness.Db.Offices.SingleAsync(x => x.Id == OfficeId);
        office.CrmStagesJson = CrmStages.Serialize(["Старт", "Финиш"]);
        await harness.Db.SaveChangesAsync();
        var response = await SeedResponseAsync(harness.Db);

        await harness.Sut.CreateCardForResponseAsync(response);

        var card = Assert.Single(harness.Db.CrmCandidateCards);
        Assert.Equal("Старт", card.Stage);
    }

    [Fact]
    public async Task Move_RejectsStageOutsideOfficeFunnel()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("move@test.local", capacity: 5, onShift: true);
        var office = await harness.Db.Offices.SingleAsync(x => x.Id == OfficeId);
        office.CrmStagesJson = CrmStages.Serialize(["А", "Б"]);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, manager.Id);
        card.Stage = "А";
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (ok, error) = await harness.Sut.MoveAsync(card.Id, CrmStages.Ticket, null, manager.Id, isAdmin: false);
        Assert.False(ok);
        Assert.Equal("Неизвестный этап.", error);

        (ok, error) = await harness.Sut.MoveAsync(card.Id, "Б", null, manager.Id, isAdmin: false);
        Assert.True(ok, error);
        Assert.Equal("Б", card.Stage);
    }

    [Fact]
    public async Task SetOfficeFunnel_RejectsEmptyOrInvalid()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);

        var (ok, error) = await harness.Sut.SetOfficeFunnelAsync(OfficeId, [], "admin");
        Assert.False(ok);
        Assert.Contains("этап", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBoard_ManagerSeesTeamReadOnlyAndOwnMineEditable()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("view@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, manager.Id);
        card.Stage = CrmStages.Lead;
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var team = await harness.Sut.GetBoardAsync(
            OfficeId,
            manager.Id,
            isAdmin: false,
            new CrmBoardQuery(Scope: CrmBoardScopes.Team));
        Assert.NotNull(team);
        Assert.Equal(CrmBoardScopes.Team, team.Scope);
        Assert.False(team.CanEdit);
        var teamCard = Assert.Single(team.Stages.SelectMany(s => s.Cards), c => c.Id == card.Id);
        Assert.Equal(manager.Id, teamCard.ManagerUserId);
        Assert.Equal("view@test.local", teamCard.ManagerName);

        var mine = await harness.Sut.GetBoardAsync(
            OfficeId,
            manager.Id,
            isAdmin: false,
            new CrmBoardQuery(Scope: CrmBoardScopes.Mine));
        Assert.NotNull(mine);
        Assert.Equal(CrmBoardScopes.Mine, mine.Scope);
        Assert.True(mine.CanEdit);
    }

    [Fact]
    public async Task GetCard_ManagerViewsOwnOfficeCardReadOnly()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var owner = await harness.CreateManagerAsync("owner@test.local", capacity: 5, onShift: true);
        var viewer = await harness.CreateManagerAsync("viewer@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, owner.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var detail = await harness.Sut.GetCardAsync(card.Id, viewer.Id, isAdmin: false);
        Assert.NotNull(detail);
        Assert.False(detail.CanEdit);
        Assert.Equal(owner.Id, detail.Card.ManagerUserId);
        Assert.Equal("owner@test.local", detail.Card.ManagerName);

        var own = await harness.Sut.GetCardAsync(card.Id, owner.Id, isAdmin: false);
        Assert.NotNull(own);
        Assert.True(own.CanEdit);
    }

    [Fact]
    public async Task GetCardAvatar_ManagerFromOwnOfficeReceivesStoredAvatar()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var owner = await harness.CreateManagerAsync("avatar-owner@test.local", capacity: 5, onShift: true);
        var viewer = await harness.CreateManagerAsync("avatar-viewer@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db);
        response.AvatarImage = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        response.AvatarContentType = "image/png";
        var card = NewCard(response.Id, owner.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var avatar = await harness.Sut.GetCardAvatarAsync(card.Id, viewer.Id, isAdmin: false);

        Assert.NotNull(avatar);
        Assert.Equal("image/png", avatar.ContentType);
        Assert.Equal(response.AvatarImage, avatar.Bytes);
    }

    [Fact]
    public async Task Tasks_AreVisibleToCreatorAndAssigneeOnly_AndCommentsKeepAuthor()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var creator = await harness.CreateManagerAsync("creator@test.local", capacity: 5, onShift: true);
        var assignee = await harness.CreateManagerAsync("assignee@test.local", capacity: 5, onShift: true);
        var outsider = await harness.CreateManagerAsync("outsider@test.local", capacity: 5, onShift: true);
        (await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == creator.Id)).FullName = "Иван Петров";
        (await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == assignee.Id)).FullName = "Мария Сидорова";
        await harness.Db.SaveChangesAsync();

        var task = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(null, "Позвонить кандидату", "Уточнить время", assignee.Id, DateTime.UtcNow.AddHours(1), CrmTaskImportances.High),
            creator.Id,
            isAdmin: false);

        Assert.NotNull(task);
        Assert.Equal(CrmTaskImportances.High, task.Importance);
        Assert.Equal("Иван Петров", task.CreatorName);
        Assert.Equal("Мария Сидорова", task.AssigneeName);
        Assert.Contains((await harness.Sut.GetTasksAsync(OfficeId, creator.Id, isAdmin: false)).Select(x => x.Id), id => id == task.Id);
        Assert.Contains((await harness.Sut.GetTasksAsync(OfficeId, assignee.Id, isAdmin: false)).Select(x => x.Id), id => id == task.Id);
        Assert.DoesNotContain((await harness.Sut.GetTasksAsync(OfficeId, outsider.Id, isAdmin: false)).Select(x => x.Id), id => id == task.Id);
        Assert.Contains((await harness.Sut.GetTasksAsync(OfficeId, "admin", isAdmin: true)).Select(x => x.Id), id => id == task.Id);

        var comment = await harness.Sut.AddTaskCommentAsync(task.Id, "Созвон согласован", creator.Id, isAdmin: false);
        Assert.NotNull(comment);
        Assert.Equal(creator.Id, comment.AuthorUserId);
        Assert.Equal("Иван Петров", comment.AuthorName);

        var detail = await harness.Sut.GetTaskAsync(task.Id, assignee.Id, isAdmin: false);
        Assert.NotNull(detail);
        Assert.True(detail.CanComplete);
        Assert.Equal(CrmTaskImportances.High, detail.Task.Importance);
        Assert.Single(detail.Comments);
        Assert.Equal("Созвон согласован", detail.Comments[0].Text);

        var adminDetail = await harness.Sut.GetTaskAsync(task.Id, "admin", isAdmin: true);
        Assert.NotNull(adminDetail);
        Assert.True(adminDetail.CanComplete);

        Assert.Null(await harness.Sut.GetTaskAsync(task.Id, outsider.Id, isAdmin: false));
        Assert.Null(await harness.Sut.AddTaskCommentAsync(task.Id, "Нет доступа", outsider.Id, isAdmin: false));
        Assert.False(await harness.Sut.CompleteTaskAsync(task.Id, creator.Id, isAdmin: false));
        Assert.True(await harness.Sut.CompleteTaskAsync(task.Id, assignee.Id, isAdmin: false));
    }

    [Fact]
    public async Task CreateTask_RejectsManagerFromAnotherOffice_AndForeignCard()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var creator = await harness.CreateManagerAsync("creator@test.local", capacity: 5, onShift: true);
        var assignee = await harness.CreateManagerAsync("assignee@test.local", capacity: 5, onShift: true);
        var foreignManager = await harness.CreateManagerAsync("foreign@test.local", capacity: 5, onShift: true);
        var foreignProfile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == foreignManager.Id);
        foreignProfile.OfficeId = Guid.NewGuid();
        await harness.Db.SaveChangesAsync();

        var foreignAssigneeTask = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(null, "Не создать", null, foreignManager.Id, null),
            creator.Id,
            isAdmin: false);
        Assert.Null(foreignAssigneeTask);

        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, creator.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var linkedTask = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(card.Id, "Проверить анкету", null, assignee.Id, null),
            assignee.Id,
            isAdmin: false);
        Assert.Null(linkedTask);
    }

    [Fact]
    public async Task TaskAuthorOrAdmin_CanEditCancelAndReopenTask()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var creator = await harness.CreateManagerAsync("creator@test.local", capacity: 5, onShift: true);
        var assignee = await harness.CreateManagerAsync("assignee@test.local", capacity: 5, onShift: true);
        var replacement = await harness.CreateManagerAsync("replacement@test.local", capacity: 5, onShift: true);
        var task = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(null, "Первичный звонок", null, assignee.Id, DateTime.UtcNow.AddHours(1)),
            creator.Id,
            isAdmin: false);
        Assert.NotNull(task);
        var taskEntity = await harness.Db.CrmTasks.SingleAsync(x => x.Id == task.Id);
        var initialReminderVersion = taskEntity.ReminderVersion;

        var denied = await harness.Sut.UpdateTaskAsync(
            task.Id,
            new CrmTaskUpdateRequest("Изменённая задача", "Описание", replacement.Id, DateTime.UtcNow.AddDays(1), CrmTaskImportances.High),
            assignee.Id,
            isAdmin: false);
        Assert.False(denied.Ok);

        var updated = await harness.Sut.UpdateTaskAsync(
            task.Id,
            new CrmTaskUpdateRequest("Изменённая задача", "Описание", replacement.Id, DateTime.UtcNow.AddDays(1), CrmTaskImportances.High),
            creator.Id,
            isAdmin: false);
        Assert.True(updated.Ok);
        var afterUpdate = await harness.Sut.GetTaskAsync(task.Id, creator.Id, isAdmin: false);
        Assert.NotNull(afterUpdate);
        Assert.True(afterUpdate.CanManage);
        Assert.Equal("Изменённая задача", afterUpdate.Task.Title);
        Assert.Equal(replacement.Id, afterUpdate.Task.AssigneeUserId);
        Assert.Equal(CrmTaskImportances.High, afterUpdate.Task.Importance);
        Assert.NotEqual(initialReminderVersion, taskEntity.ReminderVersion);

        var updatedReminderVersion = taskEntity.ReminderVersion;
        var titleOnlyUpdate = await harness.Sut.UpdateTaskAsync(
            task.Id,
            new CrmTaskUpdateRequest("Уточнённое название", "Описание", replacement.Id, taskEntity.DueAtUtc, CrmTaskImportances.High),
            creator.Id,
            isAdmin: false);
        Assert.True(titleOnlyUpdate.Ok);
        Assert.Equal(updatedReminderVersion, taskEntity.ReminderVersion);

        var pendingNotification = new CrmTaskNotificationEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            TaskId = task.Id,
            ReminderVersion = taskEntity.ReminderVersion,
            RecipientUserId = replacement.Id,
            Kind = CrmTaskNotificationKinds.DueIn24Hours,
            DueAtUtc = taskEntity.DueAtUtc!.Value,
            CreatedAtUtc = DateTime.UtcNow
        };
        harness.Db.CrmTaskNotifications.Add(pendingNotification);
        await harness.Db.SaveChangesAsync();

        var cancelDenied = await harness.Sut.CancelTaskAsync(task.Id, replacement.Id, isAdmin: false);
        Assert.False(cancelDenied.Ok);
        Assert.True((await harness.Sut.CancelTaskAsync(task.Id, creator.Id, isAdmin: false)).Ok);
        Assert.NotNull(pendingNotification.DismissedAtUtc);
        var cancelled = await harness.Sut.GetTaskAsync(task.Id, creator.Id, isAdmin: false);
        Assert.NotNull(cancelled);
        Assert.Equal(CrmTaskStatuses.Cancelled, cancelled.Task.Status);
        Assert.False(await harness.Sut.CompleteTaskAsync(task.Id, replacement.Id, isAdmin: false));

        var beforeReopenVersion = taskEntity.ReminderVersion;
        Assert.True((await harness.Sut.ReopenTaskAsync(task.Id, creator.Id, isAdmin: false)).Ok);
        Assert.NotEqual(beforeReopenVersion, taskEntity.ReminderVersion);
        Assert.True(await harness.Sut.CompleteTaskAsync(task.Id, replacement.Id, isAdmin: false));
        Assert.True((await harness.Sut.ReopenTaskAsync(task.Id, "admin", isAdmin: true)).Ok);
        var reopened = await harness.Sut.GetTaskAsync(task.Id, creator.Id, isAdmin: false);
        Assert.NotNull(reopened);
        Assert.Equal(CrmTaskStatuses.Open, reopened.Task.Status);
    }

    [Fact]
    public async Task TaskAttachment_IsAvailableToParticipantsOnly()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var creator = await harness.CreateManagerAsync("creator@test.local", capacity: 5, onShift: true);
        var assignee = await harness.CreateManagerAsync("assignee@test.local", capacity: 5, onShift: true);
        var outsider = await harness.CreateManagerAsync("outsider@test.local", capacity: 5, onShift: true);
        var task = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(null, "Получить документы", null, assignee.Id, null),
            creator.Id,
            isAdmin: false);
        Assert.NotNull(task);

        await using var uploadContent = new MemoryStream([1, 2, 3, 4]);
        var (attachment, error) = await harness.Sut.AddTaskAttachmentAsync(
            task.Id,
            uploadContent,
            uploadContent.Length,
            "..\\документы.pdf",
            "application/pdf",
            creator.Id,
            isAdmin: false);

        Assert.Null(error);
        Assert.NotNull(attachment);
        Assert.Equal("документы.pdf", attachment.FileName);
        var detail = await harness.Sut.GetTaskAsync(task.Id, assignee.Id, isAdmin: false);
        Assert.NotNull(detail);
        Assert.Single(detail.Attachments);
        Assert.Equal(attachment.Id, detail.Attachments[0].Id);

        var outsiderDownload = await harness.Sut.OpenTaskAttachmentAsync(task.Id, attachment.Id, outsider.Id, isAdmin: false);
        Assert.Null(outsiderDownload.Stream);
        var assigneeDownload = await harness.Sut.OpenTaskAttachmentAsync(task.Id, attachment.Id, assignee.Id, isAdmin: false);
        Assert.NotNull(assigneeDownload.Stream);
        await using var downloadStream = assigneeDownload.Stream!;
        using var downloaded = new MemoryStream();
        await downloadStream.CopyToAsync(downloaded);
        Assert.Equal([1, 2, 3, 4], downloaded.ToArray());
    }

    private static CrmCandidateCardEntity NewCard(Guid responseId, string? managerId = null) => new()
    {
        Id = Guid.NewGuid(),
        ResponseId = responseId,
        OfficeId = OfficeId,
        ManagerUserId = managerId,
        IsInActiveLoad = true,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
        StageChangedAtUtc = DateTime.UtcNow
    };

    private static void SeedOffice(OrbitaDbContext db, bool crmEnabled)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "CRM Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            CrmEnabled = crmEnabled
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerId,
            OfficeId = OfficeId,
            DisplayName = "w",
            MachineName = "pc",
            ApiKeyHash = "h",
            AppVersion = "1",
            MonitoringStatus = "Stopped",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static async Task<CandidateResponseEntity> SeedResponseAsync(OrbitaDbContext db, string sourceId = "src")
    {
        var person = TestCandidatePersonFactory.CreatePerson(OfficeId, fullName: "Иванов Иван", firstName: "Иван", lastName: "Иванов");
        var response = TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            phone: "79990001122",
            sourceResponseId: sourceId,
            fullName: "Иванов Иван");
        response.Status = ResponseStatuses.New;
        response.Vacancy = "Сварщик";
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(response);
        await db.SaveChangesAsync();
        return response;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public OrbitaDbContext Db { get; }
        public UserManager<IdentityUser> Users { get; }
        public CrmWorkspaceService Sut { get; }
        private string AttachmentRoot { get; }

        private Harness(ServiceProvider services, OrbitaDbContext db, UserManager<IdentityUser> users, CrmWorkspaceService sut, string attachmentRoot)
        {
            _services = services;
            Db = db;
            Users = users;
            Sut = sut;
            AttachmentRoot = attachmentRoot;
        }

        public static async Task<Harness> CreateAsync()
        {
            var dbName = Guid.NewGuid().ToString("N");
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddDbContext<OrbitaDbContext>(o => o
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddIdentityCore<IdentityUser>(o =>
                {
                    o.Password.RequireDigit = false;
                    o.Password.RequireLowercase = false;
                    o.Password.RequireUppercase = false;
                    o.Password.RequireNonAlphanumeric = false;
                    o.Password.RequiredLength = 6;
                })
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<OrbitaDbContext>();

            var sp = services.BuildServiceProvider();
            var db = sp.GetRequiredService<OrbitaDbContext>();
            await db.Database.EnsureCreatedAsync();

            var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roleManager.RoleExistsAsync(PanelRoles.Manager))
            {
                await roleManager.CreateAsync(new IdentityRole(PanelRoles.Manager));
            }

            var users = sp.GetRequiredService<UserManager<IdentityUser>>();
            var distribution = new CrmLeadDistributionService(db, users);
            var attachmentRoot = Path.Combine(Path.GetTempPath(), "orbita-crm-task-tests", Guid.NewGuid().ToString("N"));
            var attachments = new CrmTaskAttachmentStorageService(Options.Create(new CrmTaskAttachmentOptions
            {
                DataPath = attachmentRoot
            }));
            var deadlineNotifications = new CrmDeadlineNotificationService(
                db,
                TimeProvider.System,
                Options.Create(new CrmDeadlineNotificationOptions()),
                new NoOpCrmNotificationRealtimeNotifier(),
                sp.GetRequiredService<ILogger<CrmDeadlineNotificationService>>());
            var sut = new CrmWorkspaceService(
                db,
                users,
                distribution,
                taskAttachments: attachments,
                deadlineNotifications: deadlineNotifications);
            return new Harness(sp, db, users, sut, attachmentRoot);
        }

        public async Task<IdentityUser> CreateManagerAsync(string email, int capacity, bool onShift)
        {
            var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
            var result = await Users.CreateAsync(user, "Password1!");
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
            await Users.AddToRoleAsync(user, PanelRoles.Manager);
            Db.PanelUserProfiles.Add(new PanelUserProfileEntity
            {
                UserId = user.Id,
                OfficeId = OfficeId,
                CrmCapacity = capacity,
                CrmShiftActive = onShift,
                CrmShiftStartedAtUtc = onShift ? DateTime.UtcNow : null
            });
            await Db.SaveChangesAsync();
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _services.DisposeAsync();
            if (Directory.Exists(AttachmentRoot))
            {
                Directory.Delete(AttachmentRoot, recursive: true);
            }
        }
    }

    private sealed class NoOpCrmNotificationRealtimeNotifier : ICrmNotificationRealtimeNotifier
    {
        public Task NotifyAsync(
            string recipientUserId,
            CrmTaskNotificationDto notification,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}

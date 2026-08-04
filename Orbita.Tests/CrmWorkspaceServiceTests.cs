using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbita.Api.Data;
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
        Assert.True(await harness.Db.PanelUserProfiles.Where(x => x.UserId == manager.Id).Select(x => x.CrmShiftActive).SingleAsync());
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
    public async Task Tasks_AreVisibleToCreatorAndAssigneeOnly_AndCommentsKeepAuthor()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var creator = await harness.CreateManagerAsync("creator@test.local", capacity: 5, onShift: true);
        var assignee = await harness.CreateManagerAsync("assignee@test.local", capacity: 5, onShift: true);
        var outsider = await harness.CreateManagerAsync("outsider@test.local", capacity: 5, onShift: true);

        var task = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(null, "Позвонить кандидату", "Уточнить время", assignee.Id, DateTime.UtcNow.AddHours(1)),
            creator.Id,
            isAdmin: false);

        Assert.NotNull(task);
        Assert.Contains((await harness.Sut.GetTasksAsync(OfficeId, creator.Id, isAdmin: false)).Select(x => x.Id), id => id == task.Id);
        Assert.Contains((await harness.Sut.GetTasksAsync(OfficeId, assignee.Id, isAdmin: false)).Select(x => x.Id), id => id == task.Id);
        Assert.DoesNotContain((await harness.Sut.GetTasksAsync(OfficeId, outsider.Id, isAdmin: false)).Select(x => x.Id), id => id == task.Id);
        Assert.Contains((await harness.Sut.GetTasksAsync(OfficeId, "admin", isAdmin: true)).Select(x => x.Id), id => id == task.Id);

        var comment = await harness.Sut.AddTaskCommentAsync(task.Id, "Созвон согласован", creator.Id, isAdmin: false);
        Assert.NotNull(comment);
        Assert.Equal(creator.Id, comment.AuthorUserId);

        var detail = await harness.Sut.GetTaskAsync(task.Id, assignee.Id, isAdmin: false);
        Assert.NotNull(detail);
        Assert.True(detail.CanComplete);
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

        private Harness(ServiceProvider services, OrbitaDbContext db, UserManager<IdentityUser> users, CrmWorkspaceService sut)
        {
            _services = services;
            Db = db;
            Users = users;
            Sut = sut;
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
            var sut = new CrmWorkspaceService(db, users, distribution);
            return new Harness(sp, db, users, sut);
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
                CrmShiftActive = onShift
            });
            await Db.SaveChangesAsync();
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _services.DisposeAsync();
        }
    }
}

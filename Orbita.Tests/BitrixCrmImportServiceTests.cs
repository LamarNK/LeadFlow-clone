using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixCrmImportServiceTests
{
    [Fact]
    public async Task PreviewAndImport_PreservesStageManagerCommentsAndActivities_AndIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new OrbitaDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var officeId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        const string managerId = "manager-1";
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Офис 1",
            RegistrationSecretHash = "hash",
            IsEnabled = true,
            CrmEnabled = true,
            CrmStagesJson = CrmStages.Serialize(CrmStages.Default),
            CreatedAtUtc = now
        });
        db.Users.Add(new IdentityUser
        {
            Id = managerId,
            UserName = "manager@test.local",
            NormalizedUserName = "MANAGER@TEST.LOCAL",
            Email = "manager@test.local",
            NormalizedEmail = "MANAGER@TEST.LOCAL"
        });
        db.PanelUserProfiles.Add(new PanelUserProfileEntity
        {
            UserId = managerId,
            OfficeId = officeId,
            FullName = "Иванов Иван Иванович"
        });
        db.BitrixInstances.Add(new BitrixInstanceEntity
        {
            Id = instanceId,
            OfficeId = officeId,
            Name = "Bitrix Офиса 1",
            PortalHost = "b24-l7qyiy.bitrix24.ru",
            WebhookUrlProtected = protector.Protect("https://b24-l7qyiy.bitrix24.ru/rest/1/test-secret"),
            IsEnabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();

        var snapshot = new BitrixImportSnapshot(
            [new BitrixImportStage("UC_ANKETA", CrmStages.Questionnaire.ToUpperInvariant())],
            new Dictionary<string, string> { ["UF_CITY"] = "Город", ["UF_JOB"] = "Вакансия" },
            new Dictionary<long, BitrixImportUser> { [77] = new(77, "Иванов Иван") },
            [
                new BitrixImportDeal(
                    41769,
                    "Иванов Иван Иванович",
                    "UC_ANKETA",
                    CrmStages.Questionnaire.ToUpperInvariant(),
                    77,
                    now.AddDays(-2),
                    now.AddHours(-2),
                    "[p]Технические данные отклика[/p]",
                    new Dictionary<string, string?> { ["UF_CITY"] = "Подольск", ["UF_JOB"] = "Сварщик" },
                    new BitrixImportContact(501, "Иванов Иван Иванович", "+7 999 111-22-33", new Dictionary<string, string?>()),
                    [new BitrixImportComment(9001, 77, "[p]Созвонились, ждёт документы[/p]", now.AddDays(-1))],
                    [new BitrixImportActivity(8001, "[b]Перезвонить[/b]", "[p]Уточнить дату[/p]", 77, 77, now.AddDays(1), now.AddHours(-3), now.AddHours(-2), null, false)])
            ]);
        var bitrixInstances = new BitrixInstanceService(
            db,
            protector,
            null!,
            Options.Create(new OrbitaBitrixSettings()),
            new PanelAuditService(db));
        var stubClient = new StubClient(snapshot);
        var sut = new BitrixCrmImportService(
            db,
            bitrixInstances,
            stubClient,
            new PhoneNormalizer(),
            new CandidateParser());

        var (preview, previewError) = await sut.PreviewAsync(
            instanceId,
            OfficeScope.GlobalAdmin,
            officeId,
            new BitrixCrmImportPreviewRequest());

        Assert.Null(previewError);
        var row = Assert.Single(preview!.Deals);
        Assert.Equal(BitrixCrmImportActions.Create, row.Action);
        Assert.Equal(CrmStages.Questionnaire, row.StageName);
        Assert.Equal(managerId, row.OrbitaResponsibleUserId);
        Assert.Equal(1, row.CommentCount);
        Assert.Equal(1, row.ActivityCount);

        var (result, importError) = await sut.ImportAsync(
            instanceId,
            OfficeScope.GlobalAdmin,
            officeId,
            new BitrixCrmImportExecuteRequest(DealIds: [41769]),
            "admin",
            CancellationToken.None);

        Assert.Null(importError);
        Assert.Equal(1, result!.Created);
        Assert.Equal([41769L], stubClient.LastDealIds);
        var card = await db.CrmCandidateCards.Include(x => x.Response).SingleAsync();
        Assert.Equal(CrmStages.Questionnaire, card.Stage);
        Assert.Equal(managerId, card.ManagerUserId);
        Assert.Equal("Подольск", card.Response.City);
        Assert.Equal("Сварщик", card.Response.Vacancy);
        Assert.Equal("41769", card.Response.BitrixEntityId);
        Assert.Equal("Созвонились, ждёт документы", (await db.CrmCandidateNotes.SingleAsync()).Text);
        var task = await db.CrmTasks.SingleAsync();
        Assert.Equal("Перезвонить", task.Title);
        Assert.Equal("Уточнить дату", task.Description);
        Assert.Equal(managerId, task.AssigneeUserId);

        var (second, secondError) = await sut.ImportAsync(
            instanceId,
            OfficeScope.GlobalAdmin,
            officeId,
            new BitrixCrmImportExecuteRequest(DealIds: [41769]),
            "admin",
            CancellationToken.None);

        Assert.Null(secondError);
        Assert.Equal(1, second!.AlreadyImported);
        Assert.Equal(1, await db.CrmCandidateCards.CountAsync());
        Assert.Equal(1, await db.CrmCandidateNotes.CountAsync());
        Assert.Equal(1, await db.CrmTasks.CountAsync());
    }

    private sealed class StubClient(BitrixImportSnapshot snapshot) : IBitrixCrmImportClient
    {
        public IReadOnlyCollection<long>? LastDealIds { get; private set; }

        public Task<BitrixImportSnapshot> LoadAsync(
            string webhookUrl,
            int categoryId,
            IReadOnlyCollection<string> requestedStageNames,
            IReadOnlyCollection<long>? dealIds,
            CancellationToken ct)
        {
            LastDealIds = dealIds;
            return Task.FromResult(snapshot);
        }
    }
}

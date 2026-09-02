using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerSettingsTemplateServiceTests
{
    private static readonly Guid OfficeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OfficeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid WorkerA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkerB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Create_StoresPortableSettings_WithoutSecrets()
    {
        await using var db = CreateDb();
        SeedOfficesAndWorkers(db);
        var sut = CreateService(db);

        var (result, error) = await sut.CreateAsync(
            WorkerA,
            new CreateWorkerSettingsTemplateRequest(" Будни ", SamplePayload()),
            OfficeScope.ForOffice(OfficeA));

        Assert.Null(error);
        Assert.NotNull(result?.Template);
        Assert.Equal("Будни", result!.Template!.Name);
        Assert.Equal(4, result.Template.Settings.MaxConcurrentAccounts);
        Assert.True(result.Template.Settings.ResponseFilterExcludeFemale);
        Assert.Equal(55, result.Template.Settings.ResponseFilterMaxAgeMale);
        Assert.True(result.Template.Settings.AutoScheduleEnabled);
        Assert.Equal("Mon,Tue,Wed,Thu,Fri", result.Template.Settings.AutoScheduleDays);
        Assert.Equal("Добрый день", result.Template.Settings.MessengerAutoReplyMessage);
        Assert.True(result.Template.Settings.AutoDeliverToCrm);
        Assert.False(result.Template.Settings.AutoDeliverToBitrix);
        Assert.False(result.Template.Settings.MultiloginEnabled);
        Assert.Null(typeof(WorkerSettingsTemplatePayload).GetProperty("AdsPowerApiKey"));
        Assert.Null(typeof(WorkerSettingsTemplatePayload).GetProperty("RuCaptchaApiKey"));
        Assert.Null(typeof(WorkerSettingsTemplatePayload).GetProperty("MultiloginAutomationToken"));
        Assert.Null(typeof(WorkerSettingsTemplatePayload).GetProperty("AdsPowerApiBaseUrl"));
        Assert.Null(typeof(WorkerSettingsTemplatePayload).GetProperty("AdsPowerGroupId"));
        Assert.Null(typeof(WorkerSettingsTemplatePayload).GetProperty("ResponseHighlightTargetsJson"));
        Assert.Single(result.Templates);
    }

    [Fact]
    public async Task Create_RejectsDuplicateName_CaseInsensitive()
    {
        await using var db = CreateDb();
        SeedOfficesAndWorkers(db);
        var sut = CreateService(db);
        var first = await sut.CreateAsync(
            WorkerA,
            new CreateWorkerSettingsTemplateRequest("Ночной", SamplePayload()),
            OfficeScope.ForOffice(OfficeA));
        Assert.Null(first.Error);

        var duplicate = await sut.CreateAsync(
            WorkerA,
            new CreateWorkerSettingsTemplateRequest(" ночной ", SamplePayload()),
            OfficeScope.ForOffice(OfficeA));

        Assert.Null(duplicate.Result);
        Assert.Equal("Шаблон с таким названием уже есть в этом офисе.", duplicate.Error);
    }

    [Fact]
    public async Task Create_AllowsSameName_InAnotherOffice()
    {
        await using var db = CreateDb();
        SeedOfficesAndWorkers(db);
        var sut = CreateService(db);
        Assert.Null((await sut.CreateAsync(
            WorkerA,
            new CreateWorkerSettingsTemplateRequest("Общий", SamplePayload()),
            OfficeScope.ForOffice(OfficeA))).Error);

        var other = await sut.CreateAsync(
            WorkerB,
            new CreateWorkerSettingsTemplateRequest("Общий", SamplePayload()),
            OfficeScope.ForOffice(OfficeB));

        Assert.Null(other.Error);
        Assert.NotNull(other.Result?.Template);
        Assert.Equal(OfficeB, other.Result!.Template!.OfficeId);
    }

    [Fact]
    public async Task List_DoesNotReturnForeignOfficeTemplates()
    {
        await using var db = CreateDb();
        SeedOfficesAndWorkers(db);
        var sut = CreateService(db);
        await sut.CreateAsync(
            WorkerA,
            new CreateWorkerSettingsTemplateRequest("A-only", SamplePayload()),
            OfficeScope.ForOffice(OfficeA));
        await sut.CreateAsync(
            WorkerB,
            new CreateWorkerSettingsTemplateRequest("B-only", SamplePayload()),
            OfficeScope.ForOffice(OfficeB));

        var (list, error) = await sut.ListAsync(WorkerA, OfficeScope.ForOffice(OfficeA));

        Assert.Null(error);
        Assert.Equal(["A-only"], list!.Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task Mutate_RejectsCrossOfficeWorkerAndTemplate()
    {
        await using var db = CreateDb();
        SeedOfficesAndWorkers(db);
        var sut = CreateService(db);
        var created = await sut.CreateAsync(
            WorkerA,
            new CreateWorkerSettingsTemplateRequest("Только A", SamplePayload()),
            OfficeScope.ForOffice(OfficeA));
        var templateId = created.Result!.Template!.Id;

        var listFromB = await sut.ListAsync(WorkerB, OfficeScope.ForOffice(OfficeB));
        Assert.NotNull(listFromB.Templates);
        Assert.Empty(listFromB.Templates!);

        var update = await sut.UpdateAsync(
            WorkerB,
            templateId,
            new UpdateWorkerSettingsTemplateRequest("Взлом", SamplePayload()),
            OfficeScope.ForOffice(OfficeB));
        Assert.Equal("Шаблон не найден.", update.Error);

        var delete = await sut.DeleteAsync(WorkerB, templateId, OfficeScope.ForOffice(OfficeB));
        Assert.Equal("Шаблон не найден.", delete.Error);

        var foreignWorker = await sut.ListAsync(WorkerA, OfficeScope.ForOffice(OfficeB));
        Assert.Equal("Воркер не найден.", foreignWorker.Error);
    }

    [Fact]
    public async Task Create_RequiresName()
    {
        await using var db = CreateDb();
        SeedOfficesAndWorkers(db);
        var result = await CreateService(db).CreateAsync(
            WorkerA,
            new CreateWorkerSettingsTemplateRequest("   ", SamplePayload()),
            OfficeScope.ForOffice(OfficeA));

        Assert.Equal("Название шаблона обязательно.", result.Error);
    }

    [Fact]
    public async Task Create_RejectsNameLongerThanLimit()
    {
        await using var db = CreateDb();
        SeedOfficesAndWorkers(db);
        var tooLong = new string('а', WorkerSettingsTemplateRules.MaxNameLength + 1);
        var result = await CreateService(db).CreateAsync(
            WorkerA,
            new CreateWorkerSettingsTemplateRequest(tooLong, SamplePayload()),
            OfficeScope.ForOffice(OfficeA));

        Assert.Equal(
            $"Название шаблона не должно превышать {WorkerSettingsTemplateRules.MaxNameLength} символов.",
            result.Error);
    }

    [Fact]
    public async Task Update_RenamesAndOverwritesSettings()
    {
        await using var db = CreateDb();
        SeedOfficesAndWorkers(db);
        var sut = CreateService(db);
        var created = await sut.CreateAsync(
            WorkerA,
            new CreateWorkerSettingsTemplateRequest("Старый", SamplePayload()),
            OfficeScope.ForOffice(OfficeA));
        var templateId = created.Result!.Template!.Id;

        var updated = await sut.UpdateAsync(
            WorkerA,
            templateId,
            new UpdateWorkerSettingsTemplateRequest(
                "Новый",
                SamplePayload() with
                {
                    MaxConcurrentAccounts = 2,
                    AutoDeliverToCrm = false,
                    LocalChromeEnabled = false
                }),
            OfficeScope.ForOffice(OfficeA));

        Assert.Null(updated.Error);
        Assert.Equal("Новый", updated.Result!.Template!.Name);
        Assert.Equal(2, updated.Result.Template.Settings.MaxConcurrentAccounts);
        Assert.False(updated.Result.Template.Settings.AutoDeliverToCrm);
        Assert.False(updated.Result.Template.Settings.LocalChromeEnabled);
    }

    [Fact]
    public async Task Delete_RemovesTemplate()
    {
        await using var db = CreateDb();
        SeedOfficesAndWorkers(db);
        var sut = CreateService(db);
        var created = await sut.CreateAsync(
            WorkerA,
            new CreateWorkerSettingsTemplateRequest("Удалить", SamplePayload()),
            OfficeScope.ForOffice(OfficeA));

        var deleted = await sut.DeleteAsync(WorkerA, created.Result!.Template!.Id, OfficeScope.ForOffice(OfficeA));

        Assert.Null(deleted.Error);
        Assert.Empty(deleted.Result!.Templates);
        Assert.Equal(0, await db.WorkerSettingsTemplates.CountAsync());
    }

    private static WorkerSettingsTemplatePayload SamplePayload() =>
        new(
            MaxConcurrentAccounts: 4,
            ResponseFilterExcludeFemale: true,
            ResponseFilterMaxAgeMale: 55,
            ResponseHighlightEnabled: true,
            ResponseHighlightAgeBuckets: "63+",
            AutoScheduleEnabled: true,
            AutoScheduleDays: "Mon,Tue,Wed,Thu,Fri",
            AutoScheduleFromLocalTime: "07:00",
            AutoScheduleToLocalTime: "19:00",
            MessengerAutoReplyEnabled: true,
            MessengerAutoReplyMessage: "Добрый день",
            PhoneUnchangedHours: 120,
            AutoDeliverToCrm: true,
            AutoDeliverToBitrix: false,
            AdsPowerEnabled: true,
            MultiloginEnabled: false,
            LocalChromeEnabled: true);

    private static WorkerSettingsTemplateService CreateService(OrbitaDbContext db) =>
        new(db, new OfficeScopeService(db));

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedOfficesAndWorkers(OrbitaDbContext db)
    {
        db.Offices.AddRange(
            new OfficeEntity { Id = OfficeA, Name = "A", RegistrationSecretHash = "hash-a", CreatedAtUtc = DateTime.UtcNow },
            new OfficeEntity { Id = OfficeB, Name = "B", RegistrationSecretHash = "hash-b", CreatedAtUtc = DateTime.UtcNow });
        db.Workers.AddRange(
            new WorkerEntity
            {
                Id = WorkerA,
                OfficeId = OfficeA,
                DisplayName = "worker-a",
                ApiKeyHash = "h1",
                CreatedAtUtc = DateTime.UtcNow
            },
            new WorkerEntity
            {
                Id = WorkerB,
                OfficeId = OfficeB,
                DisplayName = "worker-b",
                ApiKeyHash = "h2",
                CreatedAtUtc = DateTime.UtcNow
            });
        db.SaveChanges();
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class DashboardQueryServiceAccountActivityTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task GetWorkerAccountsAsync_LastActivityUtc_UsesLatestJournalEvent()
    {
        await using var db = CreateDb();
        var now = DateTime.UtcNow;
        var monitoringAt = now.AddDays(-1).AddMinutes(-5);
        var eventAt = now.AddHours(-2);

        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "Test Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = now,
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerId,
            OfficeId = OfficeId,
            DisplayName = "worker-1",
            MachineName = "pc",
            ApiKeyHash = "hash",
            AppVersion = "1.0",
            MonitoringStatus = "Running",
            LastSeenAtUtc = now,
            CreatedAtUtc = now
        });
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = AccountId,
            DisplayName = "Avito 44",
            Status = "Ok",
            IsEnabledInPanel = true,
            LastMonitoringAt = monitoringAt,
            SubProfilesJson = """
                [{"Id":"sp-main","Name":"Основной","Category":"Работа","IsCurrent":true}]
                """,
            UpdatedAtUtc = now
        });
        db.WorkerEvents.Add(new WorkerEventEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = WorkerId,
            AccountId = AccountId,
            Level = "Warning",
            Message = "Проверка авторизации",
            Details = """{"subProfileId":"sp-main","subProfileName":"Основной"}""",
            CreatedAtUtc = eventAt
        });
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var accounts = await sut.GetWorkerAccountsAsync(WorkerId, OfficeScope.ForOffice(OfficeId));

        var account = Assert.Single(accounts);
        Assert.Equal(1, account.TodayEventErrors);
        Assert.Equal(monitoringAt, account.LastMonitoringAt);
        Assert.Equal(eventAt, account.LastActivityUtc);

        var subProfile = Assert.Single(account.SubProfiles!);
        Assert.Equal("sp-main", subProfile.Id);
        Assert.Equal(1, subProfile.TodayEventErrors);
        Assert.Equal(eventAt, subProfile.LastActivityUtc);
    }

    [Fact]
    public async Task GetWorkerAccountsAsync_SynthesizesSubProfiles_FromResponses_WhenPersistedJsonEmpty()
    {
        await using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "Test Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = now,
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerId,
            OfficeId = OfficeId,
            DisplayName = "WM1",
            MachineName = "pc",
            ApiKeyHash = "hash",
            AppVersion = "1.0",
            MonitoringStatus = "Running",
            LastSeenAtUtc = now,
            CreatedAtUtc = now
        });
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = AccountId,
            DisplayName = "Авито 30",
            Status = "Ok",
            IsEnabledInPanel = true,
            SubProfilesJson = "[]",
            UpdatedAtUtc = now
        });
        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            WorkerId = WorkerId,
            AccountId = AccountId,
            AccountName = "Авито 30",
            Source = "Avito",
            SourceResponseId = "resp-1",
            FullName = "User",
            PhoneRaw = "+79001111111",
            PhoneNormalized = "79001111111",
            AvitoSubProfileId = "438814802",
            AvitoSubProfileName = "Работа вахтой2",
            Status = ResponseStatuses.Sent,
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var accounts = await sut.GetWorkerAccountsAsync(WorkerId, OfficeScope.ForOffice(OfficeId));

        var account = Assert.Single(accounts);
        var subProfile = Assert.Single(account.SubProfiles!);
        Assert.Equal("438814802", subProfile.Id);
        Assert.Equal("Работа вахтой2", subProfile.Name);
        Assert.Equal(1, subProfile.TodayResponses);
        Assert.Equal(now, subProfile.LastActivityUtc);

        var persisted = await db.WorkerAccounts.SingleAsync(x => x.WorkerId == WorkerId && x.AccountId == AccountId);
        var persistedProfiles = JsonSerializer.Deserialize<List<WorkerSubProfileDto>>(
            persisted.SubProfilesJson,
            SubProfileJsonOptions.Deserialize);
        var persistedSubProfile = Assert.Single(persistedProfiles!);
        Assert.Equal("438814802", persistedSubProfile.Id);
        Assert.Equal("Работа вахтой2", persistedSubProfile.Name);
    }

    private static DashboardQueryService CreateService(OrbitaDbContext db) =>
        new(
            db,
            new WorkerReleaseService(Options.Create(new WorkerReleaseOptions())),
            new OfficeScopeService(db),
            new WorkerConnectionRegistry());

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;
using System.Text.Json;

namespace Orbita.Tests;

public sealed class WorkerConfigServiceTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task UpdateSubProfileEnabledAsync_AddsIdToBlacklist_WhenDisabled()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var sut = CreateService(db);
        var (success, error) = await sut.UpdateSubProfileEnabledAsync(
            WorkerId,
            AccountId,
            "sp-2",
            new UpdateWorkerSubProfileRequest(false),
            OfficeScope.ForOffice(OfficeId));

        Assert.True(success);
        Assert.Null(error);

        var account = await db.WorkerAccounts.SingleAsync();
        Assert.Equal(["sp-2"], SubProfilesDisabledIdsHelper.Parse(account.SubProfilesDisabledIdsJson));
    }

    [Fact]
    public async Task UpdateSubProfileEnabledAsync_RemovesIdFromBlacklist_WhenEnabled()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: """["sp-1","sp-2"]""");

        var sut = CreateService(db);
        var (success, error) = await sut.UpdateSubProfileEnabledAsync(
            WorkerId,
            AccountId,
            "sp-2",
            new UpdateWorkerSubProfileRequest(true),
            OfficeScope.ForOffice(OfficeId));

        Assert.True(success);
        Assert.Null(error);

        var account = await db.WorkerAccounts.SingleAsync();
        Assert.Equal(["sp-1"], SubProfilesDisabledIdsHelper.Parse(account.SubProfilesDisabledIdsJson));
    }

    [Fact]
    public async Task UpdateSubProfileEnabledAsync_ReturnsError_WhenSubProfileMissing()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var sut = CreateService(db);
        var (success, error) = await sut.UpdateSubProfileEnabledAsync(
            WorkerId,
            AccountId,
            "missing",
            new UpdateWorkerSubProfileRequest(false),
            OfficeScope.ForOffice(OfficeId));

        Assert.False(success);
        Assert.Equal("Субпрофиль не найден у аккаунта.", error);
    }

    [Fact]
    public async Task GetConfigForWorkerAsync_IncludesUpdateOffer_WhenNewerReleaseExists()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, appVersion: "1.0.0.1");

        var releaseRoot = Path.Combine(Path.GetTempPath(), $"orbita-releases-{Guid.NewGuid():N}");
        var releases = new WorkerReleaseService(Options.Create(new WorkerReleaseOptions
        {
            DataPath = releaseRoot,
            MaxUploadBytes = 1024 * 1024
        }));
        await using var package = new MemoryStream([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00], writable: false);
        var (_, uploadError) = await releases.UploadAsync(
            package,
            "Orbita.Worker.Setup-1.0.0.5.msi",
            "1.0.0.5",
            releaseNotes: "New build",
            CancellationToken.None);
        Assert.Null(uploadError);

        var sut = new WorkerConfigService(db, new OfficeScopeService(db), new NoopPanelRealtimeNotifier(), new NoopWorkerPushNotifier(), releases, CreateCaptchaSessions(db), CreateBrowserMonitorSessions(db));
        var config = await sut.GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));

        Assert.NotNull(config);
        Assert.NotNull(config!.UpdateOffer);
        Assert.Equal("1.0.0.5", config.UpdateOffer!.Version);
        Assert.Contains("1.0.0.5", config.UpdateOffer.DownloadPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetConfigForWorkerAsync_ReturnsDisabledSubProfileIds()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: """["sp-2"]""");

        var sut = CreateService(db);
        var config = await sut.GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));

        Assert.NotNull(config);
        Assert.Single(config!.Accounts);
        Assert.Equal(["sp-2"], config.Accounts[0].DisabledSubProfileIds);
    }

    private static WorkerConfigService CreateService(OrbitaDbContext db)
    {
        var releaseRoot = Path.Combine(Path.GetTempPath(), $"orbita-releases-{Guid.NewGuid():N}");
        var releases = new WorkerReleaseService(Options.Create(new WorkerReleaseOptions
        {
            DataPath = releaseRoot,
            MaxUploadBytes = 1024 * 1024
        }));
        return new(db, new OfficeScopeService(db), new NoopPanelRealtimeNotifier(), new NoopWorkerPushNotifier(), releases, CreateCaptchaSessions(db), CreateBrowserMonitorSessions(db));
    }

    private static BrowserMonitorService CreateBrowserMonitorSessions(OrbitaDbContext db) =>
        new(db, new OfficeScopeService(db), new NoopWorkerPushNotifier(), new BrowserMonitorRegistry());

    private static CaptchaSessionService CreateCaptchaSessions(OrbitaDbContext db) =>
        new(
            db,
            new OfficeScopeService(db),
            new NoopCaptchaLockNotifier(),
            new NoopPanelRealtimeNotifier(),
            new NoopWorkerPushNotifier(),
            new NoopCaptchaSessionRelayNotifier());

    private sealed class NoopCaptchaLockNotifier : ICaptchaLockNotifier
    {
        public Task NotifyLockChangedAsync(Guid workerId, Guid officeId, WorkerCaptchaLockDto lockState, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedWorkerWithAccount(
        OrbitaDbContext db,
        string disabledIdsJson = "[]",
        string appVersion = "1.0")
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "Test Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerId,
            OfficeId = OfficeId,
            DisplayName = "worker-1",
            MachineName = "pc",
            ApiKeyHash = "hash",
            AppVersion = appVersion,
            MonitoringStatus = "Stopped",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = AccountId,
            AdsPowerProfileId = "profile-1",
            DisplayName = "acc-1",
            Status = "Ok",
            SubProfilesJson = JsonSerializer.Serialize(new[]
            {
                new WorkerSubProfileDto("sp-1", "Alpha", "", false, null, null, null, null),
                new WorkerSubProfileDto("sp-2", "Beta", "", false, null, null, null, null)
            }),
            SubProfilesDisabledIdsJson = disabledIdsJson,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}

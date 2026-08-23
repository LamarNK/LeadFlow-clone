using Microsoft.AspNetCore.DataProtection;
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

        var sut = new WorkerConfigService(db, new OfficeScopeService(db), new NoopPanelRealtimeNotifier(), new NoopWorkerPushNotifier(), releases, CreateCaptchaSessions(db), CreateBrowserMonitorSessions(db), CreateAvitoSecrets());
        var config = await sut.GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));

        Assert.NotNull(config);
        Assert.NotNull(config!.UpdateOffer);
        Assert.Equal("1.0.0.5", config.UpdateOffer!.Version);
        Assert.Contains("1.0.0.5", config.UpdateOffer.DownloadPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAccountCredentialsAsync_ProtectsPasswordAndReturnsToWorkerConfig()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var secrets = CreateAvitoSecrets();
        var sut = new WorkerConfigService(
            db,
            new OfficeScopeService(db),
            new NoopPanelRealtimeNotifier(),
            new NoopWorkerPushNotifier(),
            CreateReleases(),
            CreateCaptchaSessions(db),
            CreateBrowserMonitorSessions(db),
            secrets);

        var (creds, error) = await sut.UpdateAccountCredentialsAsync(
            WorkerId,
            AccountId,
            new UpdateWorkerAccountCredentialsRequest("+79991234567", "secret-pass"),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(creds);
        Assert.Equal("+79991234567", creds!.Login);
        Assert.True(creds.HasPassword);

        var account = await db.WorkerAccounts.SingleAsync();
        Assert.Equal("+79991234567", account.AvitoLogin);
        Assert.False(string.Equals(account.AvitoPasswordProtected, "secret-pass", StringComparison.Ordinal));
        Assert.True(secrets.TryUnprotect(account.AvitoPasswordProtected, out var plain));
        Assert.Equal("secret-pass", plain);

        var config = await sut.GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));
        Assert.NotNull(config);
        Assert.Equal("+79991234567", config!.Accounts[0].AvitoLogin);
        Assert.Equal("secret-pass", config.Accounts[0].AvitoPassword);
    }

    [Fact]
    public async Task UpdateAccountCredentialsAsync_Clear_RemovesStoredSecrets()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var secrets = CreateAvitoSecrets();
        var account = await db.WorkerAccounts.SingleAsync();
        account.AvitoLogin = "user@avito";
        account.AvitoPasswordProtected = secrets.Protect("old");
        await db.SaveChangesAsync();

        var sut = CreateService(db, secrets);
        var (creds, error) = await sut.UpdateAccountCredentialsAsync(
            WorkerId,
            AccountId,
            new UpdateWorkerAccountCredentialsRequest(null, Clear: true),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(creds);
        Assert.Null(creds!.Login);
        Assert.False(creds.HasPassword);

        var reloaded = await db.WorkerAccounts.SingleAsync();
        Assert.Null(reloaded.AvitoLogin);
        Assert.Null(reloaded.AvitoPasswordProtected);
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

    [Fact]
    public async Task UpdateSettingsAsync_PersistsResponseCollectionFilters()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var sut = CreateService(db);
        var (config, error) = await sut.UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 2,
                ResponseFilterEnabled: true,
                ResponseFilterExcludeFemale: true,
                ResponseFilterExcludeMale: false,
                ResponseFilterMaxAgeMale: 62,
                ResponseFilterMaxAgeFemale: 55),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.True(config!.ResponseFilterEnabled);
        Assert.True(config.ResponseFilterExcludeFemale);
        Assert.False(config.ResponseFilterExcludeMale);
        Assert.Equal(62, config.ResponseFilterMaxAgeMale);
        Assert.Equal(55, config.ResponseFilterMaxAgeFemale);
        Assert.Equal(62, config.ResponseFilters.MaxAgeMaleInclusive);
        Assert.Equal(55, config.ResponseFilters.MaxAgeFemaleInclusive);

        var worker = await db.Workers.SingleAsync();
        Assert.True(worker.ResponseFilterEnabled);
        Assert.True(worker.ResponseFilterExcludeFemale);
        Assert.False(worker.ResponseFilterExcludeMale);
        Assert.Equal(62, worker.ResponseFilterMaxAgeMale);
        Assert.Equal(55, worker.ResponseFilterMaxAgeFemale);
    }

    [Fact]
    public async Task UpdateSettingsAsync_RejectsParallelismThatExceedsWorkerRamCapacity()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.LastRamTotalMb = WorkerParallelismRules.RamMbPerBrowser * 3;
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var (config, error) = await sut.UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(MaxConcurrentAccounts: 4),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(config);
        Assert.Contains("не более 3", error);
    }

    [Fact]
    public async Task GetConfigForWorkerAsync_CapsParallelismToWorkerRamCapacity()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.MaxConcurrentAccounts = 8;
        worker.LastRamTotalMb = WorkerParallelismRules.RamMbPerBrowser * 3;
        await db.SaveChangesAsync();

        var config = await CreateService(db).GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));

        Assert.NotNull(config);
        Assert.Equal(3, config!.MaxConcurrentAccounts);
    }

    [Fact]
    public async Task UpdateSettingsAsync_AutoEnablesFiltersAndPersistsHighlightSettings()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var highlightTargets = ResponseHighlightRules.NormalizeTargetsFormValues(
            [new ResponseHighlightTarget(AccountId, "sp-2").ToFormValue()]);

        var sut = CreateService(db);
        var (config, error) = await sut.UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 2,
                ResponseFilterEnabled: false,
                ResponseFilterExcludeMale: true,
                ResponseHighlightEnabled: true,
                ResponseHighlightAgeBuckets: "63+,bad,45+",
                ResponseHighlightTargetsJson: highlightTargets),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.True(config!.ResponseFilterEnabled);
        Assert.True(config.ResponseFilterExcludeMale);
        Assert.True(config.ResponseHighlightEnabled);
        Assert.Equal("63+,45+", config.ResponseHighlightAgeBuckets);
        Assert.Equal([new ResponseHighlightTarget(AccountId, "sp-2")], ResponseHighlightRules.ParseTargets(config.ResponseHighlightTargetsJson));

        var worker = await db.Workers.SingleAsync();
        Assert.True(worker.ResponseFilterEnabled);
        Assert.True(worker.ResponseFilterExcludeMale);
        Assert.True(worker.ResponseHighlightEnabled);
        Assert.Equal("63+,45+", worker.ResponseHighlightAgeBuckets);
        Assert.Equal([new ResponseHighlightTarget(AccountId, "sp-2")], ResponseHighlightRules.ParseTargets(worker.ResponseHighlightTargetsJson));
    }

    [Fact]
    public async Task UpdateSettingsAsync_EnablesHighlightForProfileTarget()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var highlightTargets = ResponseHighlightRules.NormalizeTargetsFormValues(
            [new ResponseHighlightTarget(AccountId).ToFormValue()]);
        var (config, error) = await CreateService(db).UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 2,
                ResponseHighlightEnabled: true,
                ResponseHighlightTargetsJson: highlightTargets),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.True(config!.ResponseHighlightEnabled);
        Assert.Null(config.ResponseHighlightAgeBuckets);
        Assert.Equal([new ResponseHighlightTarget(AccountId)], ResponseHighlightRules.ParseTargets(config.ResponseHighlightTargetsJson));
    }

    [Fact]
    public async Task UpdateSettingsAsync_PersistsAutoScheduleSettings()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var sut = CreateService(db);
        var (config, error) = await sut.UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 2,
                AutoScheduleEnabled: true,
                AutoScheduleDays: "Mon,Tue,Wed,Thu,Fri",
                AutoScheduleFromLocalTime: "07:00",
                AutoScheduleToLocalTime: "19:00"),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.True(config!.AutoScheduleEnabled);
        Assert.Equal("Mon,Tue,Wed,Thu,Fri", config.AutoScheduleDays);
        Assert.Equal("07:00", config.AutoScheduleFromLocalTime);
        Assert.Equal("19:00", config.AutoScheduleToLocalTime);

        var worker = await db.Workers.SingleAsync();
        Assert.True(worker.AutoScheduleEnabled);
        Assert.Equal("Mon,Tue,Wed,Thu,Fri", worker.AutoScheduleDays);
        Assert.Equal("07:00", worker.AutoScheduleFromLocalTime);
        Assert.Equal("19:00", worker.AutoScheduleToLocalTime);
    }

    [Fact]
    public async Task SyncAccountsAsync_PersistsAdsPowerGroupOnAccounts_AndStoresGroupCatalog()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var sut = CreateService(db);
        var synced = await sut.SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest(
                [
                    new WorkerAccountSyncItemDto("profile-1", "acc-1", "1001", "Orbita"),
                    new WorkerAccountSyncItemDto("profile-2", "acc-2", "1002", "Other")
                ],
                [
                    new AdsPowerGroupDto("0", "Ungrouped"),
                    new AdsPowerGroupDto("1001", "Orbita"),
                    new AdsPowerGroupDto("1002", "Other")
                ]));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.OrderBy(x => x.DisplayName).ToListAsync();
        Assert.Equal(2, accounts.Count);
        Assert.Equal("1001", accounts[0].AdsPowerGroupId);
        Assert.Equal("Orbita", accounts[0].AdsPowerGroupName);
        Assert.Equal("1002", accounts[1].AdsPowerGroupId);

        var worker = await db.Workers.SingleAsync();
        var groups = AdsPowerGroupsJson.Parse(worker.AdsPowerGroupsJson);
        Assert.Equal(3, groups.Count);
        Assert.Contains(groups, g => g.GroupId == "1001" && g.GroupName == "Orbita");
    }

    [Fact]
    public async Task UpdateSettingsAsync_PersistsAdsPowerGroupId()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.AdsPowerGroupsJson = AdsPowerGroupsJson.Serialize(
            [new AdsPowerGroupDto("1001", "Orbita")]);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var (config, error) = await sut.UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 1,
                AdsPowerGroupId: "1001"),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.Equal("1001", config!.AdsPowerGroupId);

        worker = await db.Workers.SingleAsync();
        Assert.Equal("1001", worker.AdsPowerGroupId);
        Assert.Equal("Orbita", worker.AdsPowerGroupName);
    }

    [Fact]
    public async Task GetConfigForWorkerAsync_ResponseFiltersDefaultOff()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var sut = CreateService(db);
        var config = await sut.GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));

        Assert.NotNull(config);
        Assert.False(config!.ResponseFilterEnabled);
        Assert.False(config.ResponseFilterExcludeFemale);
        Assert.False(config.ResponseFilterExcludeMale);
        Assert.Null(config.ResponseFilterMaxAgeMale);
        Assert.Null(config.ResponseFilterMaxAgeFemale);
        Assert.Null(config.RuCaptchaApiKey);
    }

    [Fact]
    public async Task UpdateSettingsAsync_PersistsRuCaptchaApiKey()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var sut = CreateService(db);
        var (config, error) = await sut.UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 1,
                RuCaptchaApiKey: "  rucaptcha-test-key  "),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.Equal("rucaptcha-test-key", config!.RuCaptchaApiKey);

        var worker = await db.Workers.SingleAsync();
        Assert.Equal("rucaptcha-test-key", worker.RuCaptchaApiKey);
    }

    private static WorkerConfigService CreateService(OrbitaDbContext db, AvitoAccountSecretProtector? secrets = null)
    {
        var releases = CreateReleases();
        return new(
            db,
            new OfficeScopeService(db),
            new NoopPanelRealtimeNotifier(),
            new NoopWorkerPushNotifier(),
            releases,
            CreateCaptchaSessions(db),
            CreateBrowserMonitorSessions(db),
            secrets ?? CreateAvitoSecrets());
    }

    private static WorkerReleaseService CreateReleases()
    {
        var releaseRoot = Path.Combine(Path.GetTempPath(), $"orbita-releases-{Guid.NewGuid():N}");
        return new WorkerReleaseService(Options.Create(new WorkerReleaseOptions
        {
            DataPath = releaseRoot,
            MaxUploadBytes = 1024 * 1024
        }));
    }

    private static AvitoAccountSecretProtector CreateAvitoSecrets()
    {
        var provider = DataProtectionProvider.Create(Path.Combine(Path.GetTempPath(), $"orbita-dp-{Guid.NewGuid():N}"));
        return new AvitoAccountSecretProtector(provider);
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

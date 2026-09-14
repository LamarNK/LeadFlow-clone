using LeadFlow.Core.Services.LocalChrome;
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
    private static readonly Guid LocalAccountId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

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
        Assert.Null(config.Accounts[0].AvitoCredentialsError);
    }

    [Fact]
    public async Task GetConfigForWorkerAsync_FlagsPasswordDecryptionFailure_WhenKeyRingDoesNotMatch()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var originalSecrets = CreateAvitoSecrets();
        var account = await db.WorkerAccounts.SingleAsync();
        account.AvitoLogin = "+79991234567";
        account.AvitoPasswordProtected = originalSecrets.Protect("secret-pass");
        await db.SaveChangesAsync();

        var serviceWithDifferentKeyRing = CreateService(db, CreateAvitoSecrets());

        var config = await serviceWithDifferentKeyRing.GetConfigForWorkerAsync(
            WorkerId,
            OfficeScope.ForOffice(OfficeId));

        var workerAccount = Assert.Single(config!.Accounts);
        Assert.Null(workerAccount.AvitoLogin);
        Assert.Null(workerAccount.AvitoPassword);
        Assert.Equal(
            WorkerAccountCredentialErrors.PasswordDecryptionFailed,
            workerAccount.AvitoCredentialsError);
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
    public async Task UpdateLocalAccountProfileAsync_SavesAndClearsProxy_WithoutReturningPassword()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        SeedLocalAccount(db);
        var secrets = CreateAvitoSecrets();
        var sut = CreateService(db, secrets);

        var (profile, error) = await sut.UpdateLocalAccountProfileAsync(
            WorkerId,
            LocalAccountId,
            new UpdateLocalWorkerAccountProfileRequest(
                Login: "+79990001122",
                Password: "avito-secret",
                ProxyEnabled: true,
                ProxyAddress: "203.0.113.10:8080",
                ProxyUsername: "proxy-user",
                ProxyPassword: "proxy-secret"),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(profile);
        Assert.Equal("+79990001122", profile!.Login);
        Assert.True(profile.HasPassword);
        Assert.True(profile.HasCredentials);
        Assert.True(profile.ProxyEnabled);
        Assert.Equal("203.0.113.10:8080", profile.ProxyAddress);
        Assert.Equal("proxy-user", profile.ProxyUsername);
        Assert.True(profile.HasProxyPassword);
        Assert.Equal(LocalChromeProxyRules.StatusConfigured, profile.ProxyStatus);
        Assert.Null(typeof(LocalWorkerAccountProfileDto).GetProperty("Password"));
        Assert.Null(typeof(LocalWorkerAccountProfileDto).GetProperty("ProxyPassword"));
        Assert.DoesNotContain("proxy-secret", System.Text.Json.JsonSerializer.Serialize(profile), StringComparison.Ordinal);
        Assert.DoesNotContain("avito-secret", System.Text.Json.JsonSerializer.Serialize(profile), StringComparison.Ordinal);

        var stored = await db.WorkerAccounts.SingleAsync(x => x.AccountId == LocalAccountId);
        Assert.NotEqual("proxy-secret", stored.LocalProxyPasswordProtected);
        Assert.True(secrets.TryUnprotectProxyPassword(stored.LocalProxyPasswordProtected, out var proxyPlain));
        Assert.Equal("proxy-secret", proxyPlain);

        var keep = await sut.UpdateLocalAccountProfileAsync(
            WorkerId,
            LocalAccountId,
            new UpdateLocalWorkerAccountProfileRequest(
                ProxyEnabled: true,
                ProxyAddress: "203.0.113.10:8080",
                ProxyPassword: ""),
            OfficeScope.ForOffice(OfficeId));
        Assert.Null(keep.Error);
        var kept = await db.WorkerAccounts.SingleAsync(x => x.AccountId == LocalAccountId);
        Assert.True(secrets.TryUnprotectProxyPassword(kept.LocalProxyPasswordProtected, out var still));
        Assert.Equal("proxy-secret", still);

        var cleared = await sut.UpdateLocalAccountProfileAsync(
            WorkerId,
            LocalAccountId,
            new UpdateLocalWorkerAccountProfileRequest(ClearProxyPassword: true, ProxyEnabled: true, ProxyAddress: "203.0.113.10:8080"),
            OfficeScope.ForOffice(OfficeId));
        Assert.Null(cleared.Error);
        Assert.False(cleared.Profile!.HasProxyPassword);
        var afterClear = await db.WorkerAccounts.SingleAsync(x => x.AccountId == LocalAccountId);
        Assert.Null(afterClear.LocalProxyPasswordProtected);
    }

    [Fact]
    public async Task UpdateLocalAccountProfileAsync_RejectsBadHostPort_AndAdsPower()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        SeedLocalAccount(db);
        var sut = CreateService(db);

        var bad = await sut.UpdateLocalAccountProfileAsync(
            WorkerId,
            LocalAccountId,
            new UpdateLocalWorkerAccountProfileRequest(
                ProxyEnabled: true,
                ProxyAddress: "user:pass@203.0.113.10:8080"),
            OfficeScope.ForOffice(OfficeId));
        Assert.Null(bad.Profile);
        Assert.Contains("логин", bad.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pass", bad.Error, StringComparison.Ordinal);

        var scheme = await sut.UpdateLocalAccountProfileAsync(
            WorkerId,
            LocalAccountId,
            new UpdateLocalWorkerAccountProfileRequest(
                ProxyEnabled: true,
                ProxyAddress: "http://203.0.113.10:8080"),
            OfficeScope.ForOffice(OfficeId));
        Assert.Null(scheme.Profile);

        var ads = await sut.UpdateLocalAccountProfileAsync(
            WorkerId,
            AccountId,
            new UpdateLocalWorkerAccountProfileRequest(ProxyEnabled: true, ProxyAddress: "203.0.113.10:8080"),
            OfficeScope.ForOffice(OfficeId));
        Assert.Null(ads.Profile);
        Assert.Contains("обычного браузера", ads.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateLocalAccountProfileAsync_SavesTrafficPresets_AndRejectsBadTimeout()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        SeedLocalAccount(db);
        var sut = CreateService(db);

        var economic = await sut.UpdateLocalAccountProfileAsync(
            WorkerId,
            LocalAccountId,
            new UpdateLocalWorkerAccountProfileRequest(TrafficMode: LocalChromeTrafficRules.ModeEconomic),
            OfficeScope.ForOffice(OfficeId));
        Assert.Null(economic.Error);
        Assert.Equal(LocalChromeTrafficRules.ModeEconomic, economic.Profile!.TrafficMode);
        Assert.True(economic.Profile.BlockMedia);
        Assert.True(economic.Profile.BlockAnalytics);
        Assert.True(economic.Profile.BlockPrefetch);
        Assert.False(economic.Profile.BlockImages);
        Assert.Equal(60, economic.Profile.NavigationTimeoutSeconds);

        var stored = await db.WorkerAccounts.SingleAsync(x => x.AccountId == LocalAccountId);
        Assert.Equal(LocalChromeTrafficRules.ModeEconomic, stored.LocalTrafficMode);
        Assert.True(stored.LocalBlockMedia);
        Assert.False(stored.LocalBlockImages);

        var custom = await sut.UpdateLocalAccountProfileAsync(
            WorkerId,
            LocalAccountId,
            new UpdateLocalWorkerAccountProfileRequest(
                TrafficMode: LocalChromeTrafficRules.ModeCustom,
                BlockMedia: true,
                BlockAnalytics: false,
                BlockImages: true,
                BlockFonts: false,
                BlockPrefetch: false,
                NavigationTimeoutSeconds: 30),
            OfficeScope.ForOffice(OfficeId));
        Assert.Null(custom.Error);
        Assert.Equal(LocalChromeTrafficRules.ModeCustom, custom.Profile!.TrafficMode);
        Assert.Equal(30, custom.Profile.NavigationTimeoutSeconds);

        var badTimeout = await sut.UpdateLocalAccountProfileAsync(
            WorkerId,
            LocalAccountId,
            new UpdateLocalWorkerAccountProfileRequest(NavigationTimeoutSeconds: 45),
            OfficeScope.ForOffice(OfficeId));
        Assert.Null(badTimeout.Profile);
        Assert.Contains("30", badTimeout.Error, StringComparison.Ordinal);

        var ads = await sut.UpdateLocalAccountProfileAsync(
            WorkerId,
            AccountId,
            new UpdateLocalWorkerAccountProfileRequest(
                TrafficMode: LocalChromeTrafficRules.ModeAggressive,
                BlockMedia: true),
            OfficeScope.ForOffice(OfficeId));
        Assert.Null(ads.Profile);
        Assert.Contains("обычного браузера", ads.Error, StringComparison.Ordinal);

        var adsRow = await db.WorkerAccounts.SingleAsync(x => x.AccountId == AccountId);
        Assert.Equal(LocalChromeTrafficRules.ModeNormal, adsRow.LocalTrafficMode);
        Assert.False(adsRow.LocalBlockMedia);
    }

    [Fact]
    public async Task GetConfigForWorkerAsync_SendsLocalTrafficOnlyForLocalAccounts()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        SeedLocalAccount(db);
        var sut = CreateService(db);
        await sut.UpdateLocalAccountProfileAsync(
            WorkerId,
            LocalAccountId,
            new UpdateLocalWorkerAccountProfileRequest(TrafficMode: LocalChromeTrafficRules.ModeAggressive),
            OfficeScope.ForOffice(OfficeId));

        var config = await sut.GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));
        var local = Assert.Single(config!.Accounts, a => a.AccountId == LocalAccountId);
        Assert.Equal(LocalChromeTrafficRules.ModeAggressive, local.LocalTrafficMode);
        Assert.True(local.LocalBlockImages);
        Assert.Equal(60, local.LocalNavigationTimeoutSeconds);

        var ads = Assert.Single(config.Accounts, a => a.AccountId == AccountId);
        Assert.Null(ads.LocalTrafficMode);
        Assert.False(ads.LocalBlockMedia);
        Assert.Equal(60, ads.LocalNavigationTimeoutSeconds);
    }

    [Fact]
    public async Task GetConfigForWorkerAsync_SendsLocalProxySecretOnlyOnWorkerChannel()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        SeedLocalAccount(db);
        var secrets = CreateAvitoSecrets();
        var sut = CreateService(db, secrets);
        await sut.UpdateLocalAccountProfileAsync(
            WorkerId,
            LocalAccountId,
            new UpdateLocalWorkerAccountProfileRequest(
                ProxyEnabled: true,
                ProxyAddress: "198.51.100.20:3128",
                ProxyUsername: "px",
                ProxyPassword: "worker-only-secret"),
            OfficeScope.ForOffice(OfficeId));

        var config = await sut.GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));
        var local = Assert.Single(config!.Accounts, a => a.AccountId == LocalAccountId);
        Assert.True(local.LocalProxyEnabled);
        Assert.Equal("198.51.100.20:3128", local.LocalProxyAddress);
        Assert.Equal("px", local.LocalProxyUsername);
        Assert.Equal("worker-only-secret", local.LocalProxyPassword);

        var ads = Assert.Single(config.Accounts, a => a.AccountId == AccountId);
        Assert.False(ads.LocalProxyEnabled);
        Assert.Null(ads.LocalProxyAddress);
        Assert.Null(ads.LocalProxyPassword);

        var (panelAccount, _) = await sut.UpdateAccountEnabledAsync(
            WorkerId,
            LocalAccountId,
            new UpdateWorkerAccountRequest(false),
            OfficeScope.ForOffice(OfficeId));
        Assert.NotNull(panelAccount);
        Assert.Null(panelAccount!.AvitoPassword);
        Assert.Null(panelAccount.LocalProxyPassword);
        Assert.True(panelAccount.LocalProxyEnabled);

        Assert.Null(typeof(WorkerAccountDto).GetProperty("AvitoPassword"));
        Assert.Null(typeof(WorkerAccountDto).GetProperty("LocalProxyPassword"));
        Assert.NotNull(typeof(WorkerAccountDto).GetProperty(nameof(WorkerAccountDto.HasProxyPassword)));
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
    public async Task SyncAccountsAsync_IgnoresItemsWithoutAdsPowerProfileId()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var sut = CreateService(db);
        var synced = await sut.SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest(
                [
                    new WorkerAccountSyncItemDto("profile-1", "acc-1"),
                    new WorkerAccountSyncItemDto("", "multilogin-only"),
                    new WorkerAccountSyncItemDto("   ", "whitespace")
                ]));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.ToListAsync();
        Assert.DoesNotContain(accounts, a => a.DisplayName is "multilogin-only" or "whitespace");
        Assert.All(accounts, a => Assert.False(string.IsNullOrWhiteSpace(a.AdsPowerProfileId)));
        Assert.All(accounts, a => Assert.Null(a.MultiloginProfileId));
        Assert.Contains(accounts, a => a.AdsPowerProfileId == "profile-1");
    }

    [Fact]
    public async Task SyncAccountsAsync_DoesNotDeleteMultiloginAccounts()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var mlxId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = mlxId,
            AdsPowerProfileId = string.Empty,
            MultiloginProfileId = "mlx-profile",
            MultiloginFolderId = "mlx-folder",
            DisplayName = "multilogin-acc",
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var synced = await sut.SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest([new WorkerAccountSyncItemDto("profile-1", "acc-1")]));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.ToListAsync();
        Assert.Contains(accounts, a => a.AccountId == mlxId && a.MultiloginProfileId == "mlx-profile");
        Assert.Contains(accounts, a => a.AdsPowerProfileId == "profile-1");
    }

    [Fact]
    public async Task SyncAccountsAsync_PersistsMultiloginProfileAndFolder_WithOrbitaNamespace()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        const string profileId = "mlx-profile-1";
        const string folderId = "mlx-folder-1";
        var expectedId = MultiloginAccountId.ToAccountGuid(profileId);

        var synced = await CreateService(db).SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest(
                [
                    new WorkerAccountSyncItemDto(
                        AdsPowerProfileId: "",
                        DisplayName: "from-display",
                        MultiloginProfileId: profileId,
                        MultiloginFolderId: folderId,
                        MultiloginProfileName: "mlx-acc")
                ]));

        Assert.True(synced);
        var mlx = await db.WorkerAccounts.SingleAsync(x => x.AccountId == expectedId);
        Assert.Equal(profileId, mlx.MultiloginProfileId);
        Assert.Equal(folderId, mlx.MultiloginFolderId);
        Assert.Equal("mlx-acc", mlx.MultiloginProfileName);
        Assert.Equal("mlx-acc", mlx.DisplayName);
        Assert.Equal(string.Empty, mlx.AdsPowerProfileId);
        Assert.Equal(expectedId, mlx.AccountId);
        Assert.NotEqual(AdsPowerAccountId.ToAccountGuid(profileId), mlx.AccountId);

        var ads = await db.WorkerAccounts.SingleAsync(x => x.AccountId == AccountId);
        Assert.Equal("profile-1", ads.AdsPowerProfileId);
        Assert.Null(ads.MultiloginProfileId);

        var config = await CreateService(db).GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));
        var mlxCfg = Assert.Single(config!.Accounts, a => a.AccountId == expectedId);
        Assert.Equal("Multilogin", mlxCfg.ProfileProvider);
        Assert.Equal(profileId, mlxCfg.MultiloginProfileId);
        Assert.Equal(folderId, mlxCfg.MultiloginFolderId);
        Assert.Null(typeof(WorkerAccountConfigDto).GetProperty("MultiloginAutomationToken"));
    }

    [Fact]
    public async Task SyncAccountsAsync_SkipsMultiloginWithoutFolderOrProfileId()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var synced = await CreateService(db).SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest(
                [
                    new WorkerAccountSyncItemDto(
                        AdsPowerProfileId: "",
                        DisplayName: "no-folder",
                        MultiloginProfileId: "mlx-profile",
                        MultiloginFolderId: null),
                    new WorkerAccountSyncItemDto(
                        AdsPowerProfileId: "",
                        DisplayName: "no-profile",
                        MultiloginProfileId: "",
                        MultiloginFolderId: "mlx-folder")
                ]));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.ToListAsync();
        Assert.DoesNotContain(accounts, a => a.DisplayName is "no-folder" or "no-profile");
        Assert.DoesNotContain(accounts, a => a.MultiloginProfileId == "mlx-profile");
        Assert.Contains(accounts, a => a.AdsPowerProfileId == "profile-1");
    }

    [Fact]
    public async Task SyncAccountsAsync_MultiloginOnly_DoesNotDeleteAdsPowerAccounts()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var synced = await CreateService(db).SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest(
                [
                    new WorkerAccountSyncItemDto(
                        AdsPowerProfileId: "",
                        DisplayName: "mlx-acc",
                        MultiloginProfileId: "mlx-profile",
                        MultiloginFolderId: "mlx-folder")
                ]));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.ToListAsync();
        Assert.Contains(accounts, a => a.AccountId == AccountId && a.AdsPowerProfileId == "profile-1");
        Assert.Contains(accounts, a => a.MultiloginProfileId == "mlx-profile" && a.MultiloginFolderId == "mlx-folder");
    }

    [Fact]
    public async Task SyncAccountsAsync_AdsPowerStillRemovesStaleAdsPower_KeepingMultilogin()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            AdsPowerProfileId = "stale-ads",
            DisplayName = "stale-ads",
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var synced = await CreateService(db).SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest([new WorkerAccountSyncItemDto("profile-1", "acc-1")]));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.ToListAsync();
        Assert.DoesNotContain(accounts, a => a.AdsPowerProfileId == "stale-ads");
        Assert.Contains(accounts, a => a.AdsPowerProfileId == "profile-1");
    }

    [Fact]
    public async Task SyncAccountsAsync_EmptyAdsPowerPayload_RemovesStaleAdsPower_KeepingMultilogin()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var mlxId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = mlxId,
            AdsPowerProfileId = string.Empty,
            MultiloginProfileId = "mlx-profile",
            MultiloginFolderId = "mlx-folder",
            DisplayName = "multilogin-acc",
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var synced = await CreateService(db).SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest([]));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.ToListAsync();
        Assert.DoesNotContain(accounts, a => a.AdsPowerProfileId == "profile-1");
        Assert.Contains(accounts, a => a.AccountId == mlxId && a.MultiloginProfileId == "mlx-profile");
    }

    [Fact]
    public async Task SyncAccountsAsync_MultiloginFlagEmpty_DoesNotDeleteAdsPower_RemovesStaleMultilogin()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var mlxId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = mlxId,
            AdsPowerProfileId = string.Empty,
            MultiloginProfileId = "stale-mlx",
            MultiloginFolderId = "mlx-folder",
            DisplayName = "stale-mlx",
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var synced = await CreateService(db).SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest([], Multilogin: true, ReplaceMultiloginCatalog: true));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.ToListAsync();
        Assert.Contains(accounts, a => a.AccountId == AccountId && a.AdsPowerProfileId == "profile-1");
        Assert.DoesNotContain(accounts, a => a.MultiloginProfileId == "stale-mlx");
    }

    [Fact]
    public async Task SyncAccountsAsync_IncompleteMultiloginPayload_DoesNotStaleDelete()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var staleId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = staleId,
            AdsPowerProfileId = string.Empty,
            MultiloginProfileId = "stale-mlx",
            MultiloginFolderId = "mlx-folder",
            DisplayName = "stale-mlx",
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var synced = await CreateService(db).SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest(
                [
                    new WorkerAccountSyncItemDto(
                        AdsPowerProfileId: "",
                        DisplayName: "keep",
                        MultiloginProfileId: "keep-mlx",
                        MultiloginFolderId: "keep-folder")
                ],
                Multilogin: true,
                ReplaceMultiloginCatalog: false));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.ToListAsync();
        Assert.Contains(accounts, a => a.AccountId == AccountId && a.AdsPowerProfileId == "profile-1");
        Assert.Contains(accounts, a => a.AccountId == staleId && a.MultiloginProfileId == "stale-mlx");
        Assert.Contains(accounts, a => a.MultiloginProfileId == "keep-mlx" && a.MultiloginFolderId == "keep-folder");
    }

    [Fact]
    public async Task SyncAccountsAsync_MultiloginTrueWithoutReplace_Empty_DoesNotDeleteAnyone()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var mlxId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = mlxId,
            AdsPowerProfileId = string.Empty,
            MultiloginProfileId = "keep-mlx",
            MultiloginFolderId = "mlx-folder",
            DisplayName = "keep-mlx",
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var synced = await CreateService(db).SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest([], Multilogin: true, ReplaceMultiloginCatalog: false));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.ToListAsync();
        Assert.Contains(accounts, a => a.AccountId == AccountId && a.AdsPowerProfileId == "profile-1");
        Assert.Contains(accounts, a => a.AccountId == mlxId && a.MultiloginProfileId == "keep-mlx");
    }

    [Fact]
    public async Task GetConfigForWorkerAsync_IncludesMultiloginFields_OnWorkerConfigOnly()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.MultiloginLauncherUrl = "https://launcher.mlx.yt:45001";
        worker.MultiloginCloudApiUrl = "https://api.multilogin.com";
        worker.MultiloginAutomationToken = "mlx-secret-token";
        var account = await db.WorkerAccounts.SingleAsync();
        account.MultiloginProfileId = "mlx-profile";
        account.MultiloginFolderId = "mlx-folder";
        await db.SaveChangesAsync();

        var config = await CreateService(db).GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));

        Assert.NotNull(config);
        Assert.Equal("https://launcher.mlx.yt:45001", config.MultiloginLauncherUrl);
        Assert.Equal("https://api.multilogin.com", config.MultiloginCloudApiUrl);
        Assert.Equal("mlx-secret-token", config.MultiloginAutomationToken);
        var acc = Assert.Single(config.Accounts);
        Assert.Equal("Multilogin", acc.ProfileProvider);
        Assert.Equal("mlx-profile", acc.MultiloginProfileId);
        Assert.Equal("mlx-folder", acc.MultiloginFolderId);
        Assert.Null(typeof(WorkerAccountConfigDto).GetProperty("MultiloginAutomationToken"));
        Assert.Null(typeof(WorkerAccountDto).GetProperty("MultiloginAutomationToken"));
    }

    [Fact]
    public async Task UpdateSettingsAsync_PersistsMultiloginUrls_AndStripsTrailingSlash()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var issuer = new FakeMultiloginTokenIssuer("mlx-generated-automation-token");
        var sut = CreateService(db, tokenIssuer: issuer);
        var (config, error) = await sut.UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 1,
                AdsPowerApiBaseUrl: "http://local.adspower.net:50325/",
                AdsPowerApiKey: "ads-key",
                MultiloginLauncherUrl: "https://launcher.mlx.yt:45001/",
                MultiloginCloudApiUrl: "https://api.multilogin.com/",
                MultiloginAutomationToken: "mlx-api-token"),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.Equal("http://local.adspower.net:50325", config!.AdsPowerApiBaseUrl);
        Assert.Equal("ads-key", config.AdsPowerApiKey);
        Assert.Equal("https://launcher.mlx.yt:45001", config.MultiloginLauncherUrl);
        Assert.Equal("https://api.multilogin.com", config.MultiloginCloudApiUrl);
        Assert.Equal("mlx-generated-automation-token", config.MultiloginAutomationToken);
        Assert.Equal("mlx-api-token", issuer.LastApiToken);

        var worker = await db.Workers.SingleAsync();
        Assert.Equal("https://launcher.mlx.yt:45001", worker.MultiloginLauncherUrl);
        Assert.Equal("https://api.multilogin.com", worker.MultiloginCloudApiUrl);
        Assert.Equal("mlx-generated-automation-token", worker.MultiloginAutomationToken);
        Assert.Equal("ads-key", worker.AdsPowerApiKey);
    }

    [Fact]
    public async Task UpdateSettingsAsync_EmptyLauncher_SavesDefault_AndKeepsExistingToken()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.MultiloginAutomationToken = "mlx-secret-token";
        worker.MultiloginLauncherUrl = "https://custom-launcher.example:45001";
        await db.SaveChangesAsync();

        var (config, error) = await CreateService(db).UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 1,
                MultiloginLauncherUrl: "  ",
                MultiloginAutomationToken: null),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.Equal(MultiloginWorkerSettings.DefaultLauncherUrl, config!.MultiloginLauncherUrl);
        Assert.Equal("mlx-secret-token", config.MultiloginAutomationToken);

        worker = await db.Workers.SingleAsync();
        Assert.Equal(MultiloginWorkerSettings.DefaultLauncherUrl, worker.MultiloginLauncherUrl);
        Assert.Equal("mlx-secret-token", worker.MultiloginAutomationToken);
    }

    [Fact]
    public async Task UpdateSettingsAsync_InvalidLauncher_DoesNotIncludeTokenInError()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var (config, error) = await CreateService(db).UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 1,
                MultiloginLauncherUrl: "not-a-url",
                MultiloginAutomationToken: "mlx-secret-token"),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(config);
        Assert.NotNull(error);
        Assert.DoesNotContain("mlx-secret-token", error, StringComparison.Ordinal);
        Assert.Contains("launcher Multilogin", error, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerConfigDto_OldJsonWithoutMultiloginFields_Deserializes()
    {
        const string json = """
            {"workerId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","maxConcurrentAccounts":1,"accounts":[]}
            """;
        var dto = System.Text.Json.JsonSerializer.Deserialize<WorkerConfigDto>(
            json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(dto);
        Assert.Null(dto!.MultiloginLauncherUrl);
        Assert.Null(dto.MultiloginCloudApiUrl);
        Assert.Null(dto.MultiloginAutomationToken);
        Assert.False(dto.IsMonitoringPaused);
        Assert.Empty(dto.Accounts);
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

    [Fact]
    public async Task SyncAccountsAsync_DoesNotDeleteLocalAccounts()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var localId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = localId,
            AdsPowerProfileId = string.Empty,
            LocalUserDataDir = @"D:\Orbita\ChromeProfiles\acc-1",
            DisplayName = "local-acc",
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var synced = await CreateService(db).SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest([new WorkerAccountSyncItemDto("profile-1", "acc-1")]));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.ToListAsync();
        Assert.Contains(accounts, a => a.AccountId == localId && a.LocalUserDataDir == @"D:\Orbita\ChromeProfiles\acc-1");
        Assert.Contains(accounts, a => a.AdsPowerProfileId == "profile-1");
    }

    [Fact]
    public async Task SyncAccountsAsync_EmptyAdsPowerPayload_KeepsLocalAccounts()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var localId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = localId,
            AdsPowerProfileId = string.Empty,
            LocalUserDataDir = @"D:\Orbita\ChromeProfiles\acc-1",
            DisplayName = "local-acc",
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var synced = await CreateService(db).SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest([]));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.ToListAsync();
        Assert.DoesNotContain(accounts, a => a.AdsPowerProfileId == "profile-1");
        Assert.Contains(accounts, a => a.AccountId == localId && a.LocalUserDataDir == @"D:\Orbita\ChromeProfiles\acc-1");
    }

    [Fact]
    public async Task SyncAccountsAsync_ReplaceMultiloginCatalog_DoesNotDeleteLocal()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var localId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = localId,
            AdsPowerProfileId = string.Empty,
            LocalUserDataDir = @"D:\Orbita\ChromeProfiles\acc-1",
            DisplayName = "local-acc",
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var synced = await CreateService(db).SyncAccountsAsync(
            WorkerId,
            new WorkerAccountSyncRequest([], Multilogin: true, ReplaceMultiloginCatalog: true));

        Assert.True(synced);
        var accounts = await db.WorkerAccounts.ToListAsync();
        Assert.Contains(accounts, a => a.AccountId == localId);
        Assert.Contains(accounts, a => a.AccountId == AccountId);
    }

    [Fact]
    public async Task GetConfigForWorkerAsync_IncludesLocalAccountAndChromePath()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.LocalChromeExecutablePath = @"C:\Chrome\chrome.exe";
        var localId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = localId,
            AdsPowerProfileId = string.Empty,
            LocalUserDataDir = @"D:\Orbita\ChromeProfiles\acc-1",
            DisplayName = "local-acc",
            IsEnabledInPanel = true,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var config = await CreateService(db).GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));

        Assert.NotNull(config);
        Assert.Equal(@"C:\Chrome\chrome.exe", config!.LocalChromeExecutablePath);
        var local = Assert.Single(config.Accounts, a => a.AccountId == localId);
        Assert.Equal("Local", local.ProfileProvider);
        Assert.Equal(@"D:\Orbita\ChromeProfiles\acc-1", local.LocalUserDataDir);
        Assert.Equal(string.Empty, local.AdsPowerProfileId);
        Assert.Null(local.MultiloginProfileId);
    }

    [Fact]
    public async Task CreateLocalAccountAsync_PersistsSeparateProfileFolder()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var (account, error) = await CreateService(db).CreateLocalAccountAsync(
            WorkerId,
            new CreateLocalWorkerAccountRequest("Кабинет 1", @"D:\Orbita\ChromeProfiles\acc-1"),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(account);
        Assert.Equal("Local", account!.ProfileProvider);
        Assert.Equal("Кабинет 1", account.DisplayName);
        Assert.Equal(@"D:\Orbita\ChromeProfiles\acc-1", account.LocalUserDataDir);
        Assert.Equal(string.Empty, account.AdsPowerProfileId);
        Assert.NotEqual(AccountId, account.AccountId);

        var stored = await db.WorkerAccounts.SingleAsync(x => x.AccountId == account.AccountId);
        Assert.Equal(string.Empty, stored.AdsPowerProfileId);
        Assert.Null(stored.MultiloginProfileId);
        Assert.Equal(@"D:\Orbita\ChromeProfiles\acc-1", stored.LocalUserDataDir);
    }

    [Fact]
    public async Task CreateLocalAccountAsync_WithoutPath_StoresUniqueManagedMarker()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var sut = CreateService(db);
        var (first, firstError) = await sut.CreateLocalAccountAsync(
            WorkerId,
            new CreateLocalWorkerAccountRequest("Кабинет 1"),
            OfficeScope.ForOffice(OfficeId));
        var (second, secondError) = await sut.CreateLocalAccountAsync(
            WorkerId,
            new CreateLocalWorkerAccountRequest("Кабинет 2"),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(firstError);
        Assert.Null(secondError);
        Assert.Equal("RequiresLogin", first!.Status);
        Assert.False(first.IsEnabled);
        Assert.True(LocalChromeProfileMarkers.IsManaged(first.LocalUserDataDir));
        Assert.NotEqual(first.LocalUserDataDir, second!.LocalUserDataDir);
        Assert.False(LocalChromeUserDataRules.LooksLikeForbiddenProfile(first.LocalUserDataDir!));
        Assert.Contains(first.AccountId.ToString("D"), first.LocalUserDataDir, StringComparison.OrdinalIgnoreCase);

        var resolved = LocalChromePaths.NormalizeUserDataDir(first.LocalUserDataDir, first.AccountId);
        Assert.False(resolved.EndsWith($"{Path.DirectorySeparatorChar}Default", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateLocalAccountAsync_ExistingPath_StillSupported()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var (account, error) = await CreateService(db).CreateLocalAccountAsync(
            WorkerId,
            new CreateLocalWorkerAccountRequest("Готовый", @"D:\Orbita\ChromeProfiles\legacy"),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.Equal(@"D:\Orbita\ChromeProfiles\legacy", account!.LocalUserDataDir);
        Assert.False(LocalChromeProfileMarkers.IsManaged(account.LocalUserDataDir));
        Assert.NotEqual("RequiresLogin", account.Status);
    }

    [Fact]
    public async Task CreateLocalAccountAsync_RejectsDefaultChromeProfile()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var (account, error) = await CreateService(db).CreateLocalAccountAsync(
            WorkerId,
            new CreateLocalWorkerAccountRequest("bad", @"C:\Users\user\AppData\Local\Google\Chrome\User Data"),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(account);
        Assert.Contains("стандартный профиль Chrome", error, StringComparison.Ordinal);
        Assert.Equal(1, await db.WorkerAccounts.CountAsync());
    }

    [Fact]
    public async Task CreateLocalAccountAsync_RejectsEmptyName()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var (account, error) = await CreateService(db).CreateLocalAccountAsync(
            WorkerId,
            new CreateLocalWorkerAccountRequest("  ", @"D:\Orbita\ChromeProfiles\acc-1"),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(account);
        Assert.Equal("Укажите имя аккаунта.", error);
    }

    [Fact]
    public async Task UpdateLocalAccountAsync_ChangesFolder_AndRejectsAdsPower()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var localId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = localId,
            AdsPowerProfileId = string.Empty,
            LocalUserDataDir = @"D:\Orbita\ChromeProfiles\acc-1",
            DisplayName = "local-acc",
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var (updated, error) = await sut.UpdateLocalAccountAsync(
            WorkerId,
            localId,
            new UpdateLocalWorkerAccountRequest("local-2", @"D:\Orbita\ChromeProfiles\acc-2"),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.Equal("local-2", updated!.DisplayName);
        Assert.Equal(@"D:\Orbita\ChromeProfiles\acc-2", updated.LocalUserDataDir);

        var (adsUpdate, adsError) = await sut.UpdateLocalAccountAsync(
            WorkerId,
            AccountId,
            new UpdateLocalWorkerAccountRequest("nope", @"D:\Orbita\ChromeProfiles\acc-3"),
            OfficeScope.ForOffice(OfficeId));
        Assert.Null(adsUpdate);
        Assert.Contains("обычного браузера", adsError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteLocalAccountAsync_RemovesDbRow_NotAdsPower()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var localId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = localId,
            AdsPowerProfileId = string.Empty,
            LocalUserDataDir = @"D:\Orbita\ChromeProfiles\acc-1",
            DisplayName = "local-acc",
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var (deleted, error) = await sut.DeleteLocalAccountAsync(WorkerId, localId, OfficeScope.ForOffice(OfficeId));
        Assert.True(deleted);
        Assert.Null(error);
        Assert.DoesNotContain(await db.WorkerAccounts.ToListAsync(), a => a.AccountId == localId);

        var (adsDeleted, adsError) = await sut.DeleteLocalAccountAsync(WorkerId, AccountId, OfficeScope.ForOffice(OfficeId));
        Assert.False(adsDeleted);
        Assert.Contains("обычного браузера", adsError, StringComparison.Ordinal);
        Assert.Contains(await db.WorkerAccounts.ToListAsync(), a => a.AccountId == AccountId);
    }

    [Fact]
    public async Task DeleteLocalAccountAsync_DoesNotDeleteProfileFolder()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var folder = Path.Combine(Path.GetTempPath(), $"orbita-keep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var (account, createError) = await CreateService(db).CreateLocalAccountAsync(
                WorkerId,
                new CreateLocalWorkerAccountRequest("disk", folder),
                OfficeScope.ForOffice(OfficeId));
            Assert.Null(createError);

            var (deleted, error) = await CreateService(db).DeleteLocalAccountAsync(
                WorkerId,
                account!.AccountId,
                OfficeScope.ForOffice(OfficeId));

            Assert.True(deleted);
            Assert.Null(error);
            Assert.True(Directory.Exists(folder));
            Assert.DoesNotContain(await db.WorkerAccounts.ToListAsync(), a => a.AccountId == account.AccountId);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UpdateSettingsAsync_PersistsLocalChromeExecutablePath()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var (config, error) = await CreateService(db).UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 1,
                LocalChromeExecutablePath: @"  C:\Program Files\Chromium\chrome.exe  "),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.Equal(@"C:\Program Files\Chromium\chrome.exe", config!.LocalChromeExecutablePath);
        var worker = await db.Workers.SingleAsync();
        Assert.Equal(@"C:\Program Files\Chromium\chrome.exe", worker.LocalChromeExecutablePath);
    }

    [Fact]
    public async Task GetConfigForWorkerAsync_NewWorker_EnablesAllBrowserProviders()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var config = await CreateService(db).GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));

        Assert.NotNull(config);
        Assert.True(config!.AdsPowerEnabled);
        Assert.True(config.MultiloginEnabled);
        Assert.True(config.LocalChromeEnabled);
        Assert.True(config.ShouldSyncAdsPowerCatalog);
        Assert.True(config.ShouldSyncMultiloginCatalog);
        Assert.True(config.IsBrowserProviderEnabled(Assert.Single(config.Accounts)));
        Assert.False(config.IsMonitoringPaused);
    }

    [Fact]
    public async Task GetConfigForWorkerAsync_IncludesPersistedMonitoringPause()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.IsMonitoringPaused = true;
        await db.SaveChangesAsync();

        var config = await CreateService(db).GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));

        Assert.NotNull(config);
        Assert.True(config!.IsMonitoringPaused);
        Assert.True((await db.Workers.SingleAsync()).IsMonitoringPaused);
    }

    [Fact]
    public async Task UpdateSettingsAsync_PersistsBrowserProviderToggles_AndKeepsAccounts()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);

        var (config, error) = await CreateService(db).UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 1,
                AdsPowerEnabled: false,
                MultiloginEnabled: true,
                LocalChromeEnabled: false),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.False(config!.AdsPowerEnabled);
        Assert.True(config.MultiloginEnabled);
        Assert.False(config.LocalChromeEnabled);
        Assert.False(config.ShouldSyncAdsPowerCatalog);
        Assert.True(config.ShouldSyncMultiloginCatalog);

        var worker = await db.Workers.SingleAsync();
        Assert.False(worker.AdsPowerEnabled);
        Assert.True(worker.MultiloginEnabled);
        Assert.False(worker.LocalChromeEnabled);
        Assert.Single(await db.WorkerAccounts.ToListAsync());
        Assert.Equal("profile-1", (await db.WorkerAccounts.SingleAsync()).AdsPowerProfileId);
    }

    [Fact]
    public async Task UpdateAccountEnabledAsync_RejectsEnable_WhenProviderDisabled()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.AdsPowerEnabled = false;
        var account = await db.WorkerAccounts.SingleAsync();
        account.IsEnabledInPanel = false;
        await db.SaveChangesAsync();

        var (updated, error) = await CreateService(db).UpdateAccountEnabledAsync(
            WorkerId,
            AccountId,
            new UpdateWorkerAccountRequest(true),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(updated);
        Assert.Equal(WorkerBrowserProviderMessages.DisabledHint, error);
        Assert.False((await db.WorkerAccounts.SingleAsync()).IsEnabledInPanel);
    }

    [Fact]
    public async Task UpdateAccountEnabledAsync_AllowsDisable_WhenProviderDisabled()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.AdsPowerEnabled = false;
        var account = await db.WorkerAccounts.SingleAsync();
        account.IsEnabledInPanel = true;
        await db.SaveChangesAsync();

        var (updated, error) = await CreateService(db).UpdateAccountEnabledAsync(
            WorkerId,
            AccountId,
            new UpdateWorkerAccountRequest(false),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(updated);
        Assert.False(updated!.IsEnabled);
        Assert.False((await db.WorkerAccounts.SingleAsync()).IsEnabledInPanel);
    }

    [Fact]
    public void WorkerConfigDto_ProviderFlags_DefaultTrue_AndFilterByKind()
    {
        var ads = new WorkerAccountConfigDto(
            Guid.NewGuid(),
            "ads-profile",
            "ads",
            true,
            null,
            null);
        var mlx = new WorkerAccountConfigDto(
            Guid.NewGuid(),
            AdsPowerProfileId: "",
            DisplayName: "mlx",
            IsEnabled: true,
            AdsPowerApiBaseUrl: null,
            AdsPowerApiKey: null,
            MultiloginProfileId: "mlx-profile");
        var local = new WorkerAccountConfigDto(
            Guid.NewGuid(),
            AdsPowerProfileId: "",
            DisplayName: "chrome",
            IsEnabled: true,
            AdsPowerApiBaseUrl: null,
            AdsPowerApiKey: null,
            LocalUserDataDir: @"D:\Orbita\ChromeProfiles\acc-1");

        var enabled = new WorkerConfigDto(Guid.NewGuid(), 1, null, null, [ads, mlx, local]);
        Assert.True(enabled.AdsPowerEnabled);
        Assert.True(enabled.MultiloginEnabled);
        Assert.True(enabled.LocalChromeEnabled);
        Assert.True(enabled.ShouldSyncAdsPowerCatalog);
        Assert.True(enabled.ShouldSyncMultiloginCatalog);
        Assert.True(enabled.IsBrowserProviderEnabled(ads));
        Assert.True(enabled.IsBrowserProviderEnabled(mlx));
        Assert.True(enabled.IsBrowserProviderEnabled(local));

        var filtered = enabled with { AdsPowerEnabled = false, LocalChromeEnabled = false };
        Assert.False(filtered.ShouldSyncAdsPowerCatalog);
        Assert.True(filtered.ShouldSyncMultiloginCatalog);
        Assert.False(filtered.IsBrowserProviderEnabled(ads));
        Assert.True(filtered.IsBrowserProviderEnabled(mlx));
        Assert.False(filtered.IsBrowserProviderEnabled(local));
    }

    [Fact]
    public void WorkerConfigDto_OldJsonWithoutLocalFields_Deserializes()
    {
        const string json = """
            {"workerId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","maxConcurrentAccounts":1,"accounts":[{"accountId":"cccccccc-cccc-cccc-cccc-cccccccccccc","adsPowerProfileId":"p1","displayName":"acc","isEnabled":true}]}
            """;
        var dto = System.Text.Json.JsonSerializer.Deserialize<WorkerConfigDto>(
            json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(dto);
        Assert.Null(dto!.LocalChromeExecutablePath);
        Assert.True(dto.AdsPowerEnabled);
        Assert.True(dto.MultiloginEnabled);
        Assert.True(dto.LocalChromeEnabled);
        Assert.True(dto.ShouldSyncAdsPowerCatalog);
        Assert.True(dto.ShouldSyncMultiloginCatalog);
        var acc = Assert.Single(dto.Accounts);
        Assert.Null(acc.LocalUserDataDir);
        Assert.Null(acc.ProfileProvider);
        Assert.Null(dto.PendingProviderCheck);
        Assert.Null(dto.PendingProviderSync);
    }

    [Fact]
    public async Task UpdateSettingsAsync_DisabledProvider_DoesNotOverwriteConnectionFields()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.AdsPowerApiBaseUrl = "http://keep.adspower.local:50325";
        worker.AdsPowerApiKey = "keep-ads-key";
        worker.AdsPowerGroupId = "group-keep";
        worker.MultiloginLauncherUrl = "http://keep-launcher.local:45001";
        worker.MultiloginCloudApiUrl = "https://keep-cloud.local";
        worker.MultiloginAutomationToken = "keep-mlx-token";
        worker.LocalChromeExecutablePath = @"C:\Keep\chrome.exe";
        await db.SaveChangesAsync();

        var (config, error) = await CreateService(db).UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 1,
                AdsPowerApiBaseUrl: "not-a-url",
                AdsPowerApiKey: "overwrite-ads",
                AdsPowerGroupId: "group-overwrite",
                MultiloginLauncherUrl: "also-not-a-url",
                MultiloginCloudApiUrl: "https://overwrite-cloud.local",
                MultiloginAutomationToken: "overwrite-token",
                LocalChromeExecutablePath: new string('x', 600),
                AdsPowerEnabled: false,
                MultiloginEnabled: false,
                LocalChromeEnabled: false),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.False(config!.AdsPowerEnabled);
        Assert.False(config.MultiloginEnabled);
        Assert.False(config.LocalChromeEnabled);
        Assert.Equal("http://keep.adspower.local:50325", config.AdsPowerApiBaseUrl);
        Assert.Equal("keep-ads-key", config.AdsPowerApiKey);
        Assert.Equal("http://keep-launcher.local:45001", config.MultiloginLauncherUrl);
        Assert.Equal("https://keep-cloud.local", config.MultiloginCloudApiUrl);
        Assert.Equal("keep-mlx-token", config.MultiloginAutomationToken);
        Assert.Equal(@"C:\Keep\chrome.exe", config.LocalChromeExecutablePath);

        worker = await db.Workers.SingleAsync();
        Assert.Equal("http://keep.adspower.local:50325", worker.AdsPowerApiBaseUrl);
        Assert.Equal("keep-ads-key", worker.AdsPowerApiKey);
        Assert.Equal("group-keep", worker.AdsPowerGroupId);
        Assert.Equal("http://keep-launcher.local:45001", worker.MultiloginLauncherUrl);
        Assert.Equal("keep-mlx-token", worker.MultiloginAutomationToken);
        Assert.Equal(@"C:\Keep\chrome.exe", worker.LocalChromeExecutablePath);
        Assert.Single(await db.WorkerAccounts.ToListAsync());
    }

    [Fact]
    public async Task UpdateSettingsAsync_EnabledUrlChange_ClearsStoredCheck()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.AdsPowerApiBaseUrl = "http://old.adspower.local:50325";
        worker.BrowserProviderChecksJson = BrowserProviderChecksJson.Serialize(new BrowserProviderChecksState
        {
            AdsPower = new BrowserProviderCheckSnapshot
            {
                Ok = true,
                AtUtc = DateTime.UtcNow,
                Message = "ok",
                Profiles = 4
            }
        });
        await db.SaveChangesAsync();

        var (config, error) = await CreateService(db).UpdateSettingsAsync(
            WorkerId,
            new UpdateWorkerSettingsRequest(
                MaxConcurrentAccounts: 1,
                AdsPowerApiBaseUrl: "http://new.adspower.local:50325",
                AdsPowerEnabled: true),
            OfficeScope.ForOffice(OfficeId));

        Assert.Null(error);
        Assert.NotNull(config);
        worker = await db.Workers.SingleAsync();
        Assert.Equal("http://new.adspower.local:50325", worker.AdsPowerApiBaseUrl);
        var mapped = WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.AdsPower);
        Assert.Equal(WorkerBrowserProviderStatus.Unchecked, mapped.Status);
        Assert.NotEqual(WorkerBrowserProviderStatus.Connected, mapped.Status);
    }

    [Fact]
    public async Task RequestProviderCheckAsync_RejectsDisabledAndUnknown()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.AdsPowerEnabled = false;
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        var off = await sut.RequestProviderCheckAsync(WorkerId, "AdsPower", OfficeScope.ForOffice(OfficeId));
        Assert.Null(off.Check);
        Assert.Equal(WorkerBrowserProviderMessages.ProviderOff, off.Error);

        var unknown = await sut.RequestProviderCheckAsync(WorkerId, "Bitrix", OfficeScope.ForOffice(OfficeId));
        Assert.Null(unknown.Check);
        Assert.Equal(WorkerBrowserProviderMessages.UnknownProvider, unknown.Error);
    }

    [Fact]
    public async Task RequestProviderCheckAsync_MultiloginWithoutToken_NeedsSetup()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var sut = CreateService(db);

        var result = await sut.RequestProviderCheckAsync(WorkerId, "Multilogin", OfficeScope.ForOffice(OfficeId));
        Assert.Null(result.Check);
        Assert.Equal(WorkerBrowserProviderMessages.NeedsToken, result.Error);
    }

    [Fact]
    public async Task RequestProviderCheckAsync_QueuesOnWorker_AndGetConfigDoesNotConsume()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var sut = CreateService(db);

        var queued = await sut.RequestProviderCheckAsync(WorkerId, "AdsPower", OfficeScope.ForOffice(OfficeId));
        Assert.Null(queued.Error);
        Assert.Equal(WorkerBrowserProviderStatus.Checking, queued.Check!.Status);
        Assert.True(queued.Check.CanCheck is false);

        var config = await sut.GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));
        Assert.NotNull(config!.PendingProviderCheck);
        Assert.Equal(WorkerBrowserProviderKinds.AdsPower, config.PendingProviderCheck!.Provider);

        var again = await sut.GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));
        Assert.Equal(WorkerBrowserProviderKinds.AdsPower, again!.PendingProviderCheck!.Provider);

        var worker = await db.Workers.SingleAsync();
        var localWhileBusy = WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.Local);
        Assert.False(localWhileBusy.CanCheck);
        Assert.NotEqual(WorkerBrowserProviderStatus.Checking, localWhileBusy.Status);
    }

    [Fact]
    public async Task RequestProviderCheckAsync_SecondRequest_DoesNotOverwritePending_EvenForOtherProvider()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var queuedAt = DateTime.UtcNow.AddMinutes(-5);
        var worker = await db.Workers.SingleAsync();
        worker.PendingBrowserProviderCheck = WorkerBrowserProviderKinds.AdsPower;
        worker.PendingBrowserProviderCheckAtUtc = queuedAt;
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var same = await sut.RequestProviderCheckAsync(WorkerId, "AdsPower", OfficeScope.ForOffice(OfficeId));
        Assert.Null(same.Check);
        Assert.Equal(WorkerBrowserProviderMessages.CheckAlreadyQueued, same.Error);

        var other = await sut.RequestProviderCheckAsync(WorkerId, "Local", OfficeScope.ForOffice(OfficeId));
        Assert.Null(other.Check);
        Assert.Equal(WorkerBrowserProviderMessages.CheckAlreadyQueued, other.Error);

        worker = await db.Workers.SingleAsync();
        Assert.Equal(WorkerBrowserProviderKinds.AdsPower, worker.PendingBrowserProviderCheck);
        Assert.Equal(queuedAt, worker.PendingBrowserProviderCheckAtUtc);
    }

    [Fact]
    public async Task RequestProviderSyncAsync_SecondRequest_DoesNotOverwritePending_EvenForOtherProvider()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var queuedAt = DateTime.UtcNow.AddMinutes(-4);
        var worker = await db.Workers.SingleAsync();
        worker.MultiloginAutomationToken = "keep-mlx-token";
        worker.PendingBrowserProviderSync = WorkerBrowserProviderKinds.AdsPower;
        worker.PendingBrowserProviderSyncAtUtc = queuedAt;
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var same = await sut.RequestProviderSyncAsync(WorkerId, "AdsPower", OfficeScope.ForOffice(OfficeId));
        Assert.Null(same.Check);
        Assert.Equal(WorkerBrowserProviderMessages.SyncAlreadyQueued, same.Error);

        var other = await sut.RequestProviderSyncAsync(WorkerId, "Multilogin", OfficeScope.ForOffice(OfficeId));
        Assert.Null(other.Check);
        Assert.Equal(WorkerBrowserProviderMessages.SyncAlreadyQueued, other.Error);

        worker = await db.Workers.SingleAsync();
        Assert.Equal(WorkerBrowserProviderKinds.AdsPower, worker.PendingBrowserProviderSync);
        Assert.Equal(queuedAt, worker.PendingBrowserProviderSyncAtUtc);
    }

    [Fact]
    public async Task MapProviderCheck_PendingCheck_BlocksCanCheckOnOtherProviders()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.PendingBrowserProviderCheck = WorkerBrowserProviderKinds.AdsPower;
        worker.PendingBrowserProviderCheckAtUtc = DateTime.UtcNow;
        worker.MultiloginAutomationToken = "mlx";
        await db.SaveChangesAsync();

        var ads = WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.AdsPower);
        var mlx = WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.Multilogin);
        var local = WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.Local);

        Assert.Equal(WorkerBrowserProviderStatus.Checking, ads.Status);
        Assert.False(ads.CanCheck);
        Assert.False(mlx.CanCheck);
        Assert.False(local.CanCheck);
        Assert.NotEqual(WorkerBrowserProviderStatus.Checking, mlx.Status);
        Assert.NotEqual(WorkerBrowserProviderStatus.Checking, local.Status);
        Assert.NotEqual(WorkerBrowserProviderStatus.Connected, ads.Status);
    }

    [Fact]
    public async Task MapProviderCheck_PendingSync_BlocksCanSyncOnOtherProviders()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.MultiloginAutomationToken = "mlx";
        worker.PendingBrowserProviderSync = WorkerBrowserProviderKinds.AdsPower;
        worker.PendingBrowserProviderSyncAtUtc = DateTime.UtcNow;
        worker.BrowserProviderChecksJson = BrowserProviderChecksJson.Serialize(new BrowserProviderChecksState
        {
            AdsPower = new BrowserProviderCheckSnapshot { Ok = true, AtUtc = DateTime.UtcNow, Message = "ok", Profiles = 1 },
            Multilogin = new BrowserProviderCheckSnapshot { Ok = true, AtUtc = DateTime.UtcNow, Message = "ok", Profiles = 1 }
        });
        await db.SaveChangesAsync();

        var ads = WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.AdsPower);
        var mlx = WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.Multilogin);

        Assert.Equal(WorkerBrowserProviderStatus.Connected, ads.Status);
        Assert.Equal(WorkerBrowserProviderStatus.Connected, mlx.Status);
        Assert.False(ads.CanSync);
        Assert.False(mlx.CanSync);
        Assert.True(ads.CanCheck);
        Assert.True(mlx.CanCheck);
    }

    [Fact]
    public async Task RequestProviderSyncAsync_Local_IsUnknown()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var result = await CreateService(db).RequestProviderSyncAsync(
            WorkerId,
            WorkerBrowserProviderKinds.Local,
            OfficeScope.ForOffice(OfficeId));
        Assert.Null(result.Check);
        Assert.Equal(WorkerBrowserProviderMessages.UnknownProvider, result.Error);
    }

    [Fact]
    public async Task ReportProviderCheckAsync_SanitizesSecret_AndClearsPending()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.AdsPowerApiKey = "ads-live-secret-key";
        worker.PendingBrowserProviderCheck = WorkerBrowserProviderKinds.AdsPower;
        worker.PendingBrowserProviderCheckAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var (ok, error) = await sut.ReportProviderCheckAsync(
            WorkerId,
            new ReportWorkerBrowserProviderCheckRequest(
                WorkerBrowserProviderKinds.AdsPower,
                Success: true,
                Message: "Local API ok Authorization: Bearer ads-live-secret-key profiles=3",
                ProfileCount: 3,
                GroupCount: 1,
                Groups: [new AdsPowerGroupDto("g-1", "Москва")]));

        Assert.True(ok);
        Assert.Null(error);

        worker = await db.Workers.SingleAsync();
        Assert.Null(worker.PendingBrowserProviderCheck);
        Assert.Contains("profiles=3", worker.BrowserProviderChecksJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ads-live-secret-key", worker.BrowserProviderChecksJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", worker.BrowserProviderChecksJson, StringComparison.OrdinalIgnoreCase);

        var mapped = WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.AdsPower);
        Assert.Equal(WorkerBrowserProviderStatus.Connected, mapped.Status);
        Assert.Equal(3, mapped.ProfileCount);
        Assert.True(mapped.CanSync);
        Assert.DoesNotContain("ads-live-secret-key", mapped.Message ?? "", StringComparison.Ordinal);
        var groups = AdsPowerGroupsJson.Parse(worker.AdsPowerGroupsJson);
        var group = Assert.Single(groups);
        Assert.Equal("g-1", group.GroupId);
        Assert.Equal("Москва", group.GroupName);
    }

    [Fact]
    public async Task MapProviderCheck_NeverFakesConnected()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();

        var uncheckedStatus = WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.AdsPower);
        Assert.Equal(WorkerBrowserProviderStatus.Unchecked, uncheckedStatus.Status);
        Assert.False(uncheckedStatus.CanSync);
        Assert.True(uncheckedStatus.CanCheck);

        worker.BrowserProviderChecksJson = BrowserProviderChecksJson.Serialize(new BrowserProviderChecksState
        {
            AdsPower = new BrowserProviderCheckSnapshot
            {
                Ok = false,
                AtUtc = DateTime.UtcNow,
                Message = "down"
            }
        });
        var error = WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.AdsPower);
        Assert.Equal(WorkerBrowserProviderStatus.Error, error.Status);
        Assert.NotEqual(WorkerBrowserProviderStatus.Connected, error.Status);
        Assert.False(error.CanSync);

        worker.AdsPowerEnabled = false;
        worker.BrowserProviderChecksJson = BrowserProviderChecksJson.Serialize(new BrowserProviderChecksState
        {
            AdsPower = new BrowserProviderCheckSnapshot
            {
                Ok = true,
                AtUtc = DateTime.UtcNow,
                Message = "ok",
                Profiles = 9
            }
        });
        var disabled = WorkerConfigService.MapProviderCheck(worker, WorkerBrowserProviderKinds.AdsPower);
        Assert.Equal(WorkerBrowserProviderStatus.Disabled, disabled.Status);
        Assert.NotEqual(WorkerBrowserProviderStatus.Connected, disabled.Status);
        Assert.False(disabled.CanCheck);
        Assert.False(disabled.CanSync);
    }

    [Fact]
    public async Task GetConfigForWorkerAsync_IncludesPendingProviderJobs()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db);
        var worker = await db.Workers.SingleAsync();
        worker.PendingBrowserProviderCheck = WorkerBrowserProviderKinds.Local;
        worker.PendingBrowserProviderCheckAtUtc = DateTime.UtcNow.AddMinutes(-1);
        worker.PendingBrowserProviderSync = WorkerBrowserProviderKinds.AdsPower;
        worker.PendingBrowserProviderSyncAtUtc = DateTime.UtcNow.AddMinutes(-2);
        await db.SaveChangesAsync();

        var config = await CreateService(db).GetConfigForWorkerAsync(WorkerId, OfficeScope.ForOffice(OfficeId));
        Assert.Equal(WorkerBrowserProviderKinds.Local, config!.PendingProviderCheck!.Provider);
        Assert.Equal(WorkerBrowserProviderKinds.AdsPower, config.PendingProviderSync!.Provider);
        Assert.True(config.AdsPowerEnabled);
        Assert.True(config.LocalChromeEnabled);
    }

    private static WorkerConfigService CreateService(
        OrbitaDbContext db,
        AvitoAccountSecretProtector? secrets = null,
        IMultiloginAutomationTokenIssuer? tokenIssuer = null)
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
            secrets ?? CreateAvitoSecrets(),
            tokenIssuer);
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

    private sealed class FakeMultiloginTokenIssuer(string issuedToken) : IMultiloginAutomationTokenIssuer
    {
        public string? LastApiToken { get; private set; }

        public Task<string> IssueAsync(string? cloudApiUrl, string apiToken, CancellationToken cancellationToken = default)
        {
            LastApiToken = apiToken;
            return Task.FromResult(issuedToken);
        }
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

    private static void SeedLocalAccount(OrbitaDbContext db)
    {
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = LocalAccountId,
            AdsPowerProfileId = string.Empty,
            LocalUserDataDir = @"D:\Orbita\ChromeProfiles\acc-1",
            DisplayName = "local-acc",
            Status = "RequiresLogin",
            IsEnabled = false,
            IsEnabledInPanel = false,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}

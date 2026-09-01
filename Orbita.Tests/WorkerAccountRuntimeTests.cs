using LeadFlow.Core.Models;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Captcha;
using LeadFlow.Core.Services.LocalChrome;
using LeadFlow.Core.Services.Multilogin;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;
using PuppeteerSharp;
using System.Reflection;

namespace Orbita.Tests;

public sealed class WorkerAccountRuntimeTests
{
    [Fact]
    public void Resolve_AdsPowerAccount_SelectsAdsPower()
    {
        var account = AdsPowerAccount();
        Assert.Equal(WorkerAccountRuntimeKind.AdsPower, WorkerAccountRuntime.Resolve(account));
        Assert.True(WorkerAccountRuntime.IsAdsPower(account));
        Assert.False(WorkerAccountRuntime.IsMultilogin(account));
        Assert.False(WorkerAccountRuntime.IsLocal(account));
    }

    [Fact]
    public void Resolve_MultiloginAccount_SelectsMultilogin()
    {
        var account = MultiloginAccount();
        Assert.Equal(WorkerAccountRuntimeKind.Multilogin, WorkerAccountRuntime.Resolve(account));
        Assert.True(WorkerAccountRuntime.IsMultilogin(account));
        Assert.False(WorkerAccountRuntime.IsAdsPower(account));
        Assert.False(WorkerAccountRuntime.IsLocal(account));
    }

    [Fact]
    public void Resolve_LocalAccount_SelectsLocal()
    {
        var account = LocalAccount();
        Assert.Equal(WorkerAccountRuntimeKind.Local, WorkerAccountRuntime.Resolve(account));
        Assert.True(WorkerAccountRuntime.IsLocal(account));
        Assert.True(WorkerAccountRuntime.IsLocalProvider(account));
        Assert.False(WorkerAccountRuntime.IsAdsPower(account));
        Assert.False(WorkerAccountRuntime.IsMultilogin(account));
        Assert.Equal(account.Id.ToString("D"), WorkerAccountRuntime.MonitorProfileId(account));
    }

    [Fact]
    public void Resolve_LocalWithoutUserDataDir_IsLocalProviderButNotValid()
    {
        var account = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "local-empty",
            ProfileProvider = AvitoProfileProvider.Local
        };

        Assert.Equal(WorkerAccountRuntimeKind.Local, WorkerAccountRuntime.Resolve(account));
        Assert.True(WorkerAccountRuntime.IsLocalProvider(account));
        Assert.False(WorkerAccountRuntime.IsLocal(account));
        Assert.False(WorkerAccountRuntime.IsAdsPower(account));
    }

    [Fact]
    public void IsBrowserProviderEnabled_FollowsProviderFlags()
    {
        Assert.True(WorkerAccountRuntime.IsBrowserProviderEnabled(AdsPowerAccount(), true, false, false));
        Assert.False(WorkerAccountRuntime.IsBrowserProviderEnabled(AdsPowerAccount(), false, true, true));
        Assert.True(WorkerAccountRuntime.IsBrowserProviderEnabled(MultiloginAccount(), false, true, false));
        Assert.False(WorkerAccountRuntime.IsBrowserProviderEnabled(MultiloginAccount(), true, false, true));
        Assert.True(WorkerAccountRuntime.IsBrowserProviderEnabled(LocalAccount(), false, false, true));
        Assert.False(WorkerAccountRuntime.IsBrowserProviderEnabled(LocalAccount(), true, true, false));
    }

    [Fact]
    public void Mapper_CopiesMultiloginProfileAndFolder_FromConfig()
    {
        var dto = new WorkerAccountConfigDto(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            AdsPowerProfileId: "",
            DisplayName: "mlx",
            IsEnabled: true,
            AdsPowerApiBaseUrl: null,
            AdsPowerApiKey: null,
            ProfileProvider: "Multilogin",
            MultiloginProfileId: "profile-mlx",
            MultiloginFolderId: "folder-mlx");
        var config = new WorkerConfigDto(
            Guid.NewGuid(),
            1,
            "http://local.adspower.net:50325",
            "ads-key",
            [dto],
            MultiloginLauncherUrl: "https://launcher.mlx.yt:45001/",
            MultiloginAutomationToken: "mlx-secret-token");

        var account = WorkerAccountRuntimeMapper.ToAccount(dto, config, "http://local.adspower.net:50325");

        Assert.Equal(AvitoProfileProvider.Multilogin, account.ProfileProvider);
        Assert.Equal("profile-mlx", account.MultiloginProfileId);
        Assert.Equal("folder-mlx", account.MultiloginFolderId);
        Assert.Equal("mlx", account.MultiloginProfileName);
        Assert.Equal("https://launcher.mlx.yt:45001", account.MultiloginLauncherUrl);
        Assert.Equal("mlx-secret-token", account.MultiloginAutomationToken);
        Assert.Equal(WorkerAccountRuntimeKind.Multilogin, WorkerAccountRuntime.Resolve(account));
    }

    [Fact]
    public void Mapper_OldAdsPowerDto_StaysAdsPower()
    {
        var dto = new WorkerAccountConfigDto(
            Guid.NewGuid(),
            "ads-user",
            "acc",
            true,
            "http://local.adspower.net:50325",
            "key");
        var config = new WorkerConfigDto(Guid.NewGuid(), 1, dto.AdsPowerApiBaseUrl, "key", [dto]);

        var account = WorkerAccountRuntimeMapper.ToAccount(dto, config, "http://fallback");

        Assert.Equal(AvitoProfileProvider.AdsPower, account.ProfileProvider);
        Assert.Equal("ads-user", account.AdsPowerProfileId);
        Assert.Null(account.MultiloginProfileId);
        Assert.Null(account.MultiloginProfileName);
        Assert.Equal(WorkerAccountRuntimeKind.AdsPower, WorkerAccountRuntime.Resolve(account));
    }

    [Fact]
    public void Mapper_CopiesLocalUserDataDirAndWorkerChromePath()
    {
        var dto = new WorkerAccountConfigDto(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            AdsPowerProfileId: "",
            DisplayName: "chrome-acc",
            IsEnabled: true,
            AdsPowerApiBaseUrl: null,
            AdsPowerApiKey: null,
            ProfileProvider: "Local",
            LocalUserDataDir: @"D:\Orbita\ChromeProfiles\acc-1");
        var config = new WorkerConfigDto(
            Guid.NewGuid(),
            1,
            "http://local.adspower.net:50325",
            "ads-key",
            [dto],
            LocalChromeExecutablePath: @"C:\Program Files\Google\Chrome\Application\chrome.exe");

        var account = WorkerAccountRuntimeMapper.ToAccount(dto, config, "http://local.adspower.net:50325");

        Assert.Equal(AvitoProfileProvider.Local, account.ProfileProvider);
        Assert.Equal(@"D:\Orbita\ChromeProfiles\acc-1", account.BrowserProfilePath);
        Assert.Equal(@"C:\Program Files\Google\Chrome\Application\chrome.exe", account.LocalChromeExecutablePath);
        Assert.Equal(string.Empty, account.AdsPowerProfileId);
        Assert.Null(account.MultiloginProfileId);
        Assert.Equal(WorkerAccountRuntimeKind.Local, WorkerAccountRuntime.Resolve(account));
    }

    [Fact]
    public void Mapper_InfersLocal_WhenUserDataDirSetAndProviderEmpty()
    {
        var dto = new WorkerAccountConfigDto(
            Guid.NewGuid(),
            AdsPowerProfileId: "",
            DisplayName: "inferred",
            IsEnabled: true,
            AdsPowerApiBaseUrl: null,
            AdsPowerApiKey: null,
            LocalUserDataDir: @"D:\profiles\one");
        var config = new WorkerConfigDto(Guid.NewGuid(), 1, null, null, [dto]);

        var account = WorkerAccountRuntimeMapper.ToAccount(dto, config, "http://fallback");

        Assert.Equal(AvitoProfileProvider.Local, account.ProfileProvider);
        Assert.Equal(@"D:\profiles\one", account.BrowserProfilePath);
        Assert.Equal(WorkerAccountRuntimeKind.Local, WorkerAccountRuntime.Resolve(account));
    }

    [Fact]
    public void Mapper_CopiesLocalProxy_OnlyWhenEnabled()
    {
        var enabled = new WorkerAccountConfigDto(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            AdsPowerProfileId: "",
            DisplayName: "chrome-acc",
            IsEnabled: true,
            AdsPowerApiBaseUrl: null,
            AdsPowerApiKey: null,
            ProfileProvider: "Local",
            LocalUserDataDir: @"D:\Orbita\ChromeProfiles\acc-1",
            LocalProxyEnabled: true,
            LocalProxyAddress: "203.0.113.10:8080",
            LocalProxyUsername: "px",
            LocalProxyPassword: "proxy-secret");
        var config = new WorkerConfigDto(Guid.NewGuid(), 1, null, null, [enabled]);
        var account = WorkerAccountRuntimeMapper.ToAccount(enabled, config, "http://fallback");

        Assert.Equal("http", account.ProxyType);
        Assert.Equal("203.0.113.10:8080", account.ProxyAddress);
        Assert.Equal("px", account.ProxyUsername);
        Assert.Equal("proxy-secret", account.ProxyPassword);

        var captcha = GeeTestV4TaskOptions.FromBrowserProfile(
            account.AssignedUserAgent,
            account.ProxyType,
            account.ProxyAddress,
            account.ProxyUsername,
            account.ProxyPassword);
        Assert.True(captcha.UsesSuppliedProxy);
        Assert.Equal("http", captcha.Proxy!.Type);
        Assert.Equal("203.0.113.10", captcha.Proxy.Address);
        Assert.Equal(8080, captcha.Proxy.Port);
        Assert.Equal("px", captcha.Proxy.Login);
        Assert.Equal("proxy-secret", captcha.Proxy.Password);

        var disabled = enabled with { LocalProxyEnabled = false, LocalProxyPassword = "should-not-copy" };
        var withoutProxy = WorkerAccountRuntimeMapper.ToAccount(disabled, config, "http://fallback");
        Assert.True(string.IsNullOrWhiteSpace(withoutProxy.ProxyAddress));
        Assert.Null(withoutProxy.ProxyPassword);
        var launch = LocalChromeLaunchOptionsFactory.FromAccount(withoutProxy);
        Assert.False(launch.ProxyEnabled);
        Assert.Null(launch.ChromiumArgs);
    }

    [Fact]
    public void Mapper_CopiesLocalTrafficSettings()
    {
        var dto = new WorkerAccountConfigDto(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            AdsPowerProfileId: "",
            DisplayName: "chrome-acc",
            IsEnabled: true,
            AdsPowerApiBaseUrl: null,
            AdsPowerApiKey: null,
            ProfileProvider: "Local",
            LocalUserDataDir: @"D:\Orbita\ChromeProfiles\acc-1",
            LocalTrafficMode: LocalChromeTrafficRules.ModeAggressive,
            LocalBlockMedia: true,
            LocalBlockAnalytics: true,
            LocalBlockImages: true,
            LocalBlockFonts: true,
            LocalBlockPrefetch: true,
            LocalNavigationTimeoutSeconds: 90);
        var config = new WorkerConfigDto(Guid.NewGuid(), 1, null, null, [dto]);
        var account = WorkerAccountRuntimeMapper.ToAccount(dto, config, "http://fallback");

        Assert.Equal(LocalChromeTrafficRules.ModeCustom, account.LocalTrafficMode);
        Assert.True(account.LocalBlockMedia);
        Assert.True(account.LocalBlockImages);
        Assert.Equal(90, account.LocalNavigationTimeoutSeconds);

        var adsDto = new WorkerAccountConfigDto(
            Guid.NewGuid(),
            "ads-user",
            "acc",
            true,
            null,
            null,
            LocalBlockMedia: true,
            LocalNavigationTimeoutSeconds: 30);
        var ads = WorkerAccountRuntimeMapper.ToAccount(
            adsDto,
            new WorkerConfigDto(Guid.NewGuid(), 1, null, null, [adsDto]),
            "http://fallback");
        Assert.Equal(LocalChromeTrafficRules.ModeNormal, ads.LocalTrafficMode);
        Assert.False(ads.LocalBlockMedia);
        Assert.Equal(60, ads.LocalNavigationTimeoutSeconds);
    }

    [Fact]
    public async Task Factory_LocalProxy_PassesHttpLaunchArg()
    {
        var ads = new FakeAdsPowerAutomation();
        var mlx = new FakeMultiloginConnector();
        var chrome = new FakeLocalChromeLauncher();
        var sut = new WorkerAccountSessionFactory(ads, mlx, chrome);
        var account = LocalAccount();
        account.ProxyType = "http";
        account.ProxyAddress = "203.0.113.10:8080";
        account.ProxyUsername = "px";
        account.ProxyPassword = "secret";

        var opened = await sut.OpenAsync(
            account,
            new AdsPowerConnectionOptions("", null),
            reportStartupStage: null,
            CancellationToken.None);

        Assert.NotNull(chrome.LastOptions);
        Assert.True(chrome.LastOptions!.ProxyEnabled);
        Assert.Equal("203.0.113.10:8080", chrome.LastOptions.ProxyServer);
        Assert.Equal(["--proxy-server=http://203.0.113.10:8080"], chrome.LastOptions.ChromiumArgs);
        Assert.DoesNotContain("secret", chrome.LastOptions.ChromiumArgs![0], StringComparison.Ordinal);
        Assert.Equal("px", chrome.LastOptions.ProxyUsername);
        await opened.DisposeAsync();
    }

    [Fact]
    public async Task Factory_AdsPowerPath_DoesNotOpenMultilogin()
    {
        var ads = new FakeAdsPowerAutomation();
        var mlx = new FakeMultiloginConnector();
        var sut = new WorkerAccountSessionFactory(ads, mlx);

        var opened = await sut.OpenAsync(
            AdsPowerAccount(),
            new AdsPowerConnectionOptions("http://local.adspower.net:50325", "key"),
            reportStartupStage: null,
            CancellationToken.None);

        Assert.Equal(WorkerAccountRuntimeKind.AdsPower, opened.Runtime);
        Assert.Equal(1, ads.OpenAdsPowerCount);
        Assert.Equal(0, ads.OpenOnConnectedCount);
        Assert.Equal(0, mlx.OpenCount);
        await opened.DisposeAsync();
        Assert.Equal(1, ads.CloseCount);
        Assert.Equal(0, mlx.StopCount);
    }

    [Fact]
    public async Task Factory_MultiloginPath_StartsConnectsThenStops()
    {
        var ads = new FakeAdsPowerAutomation();
        var mlx = new FakeMultiloginConnector();
        var sut = new WorkerAccountSessionFactory(ads, mlx);

        var opened = await sut.OpenAsync(
            MultiloginAccount(),
            new AdsPowerConnectionOptions("http://unused", null),
            reportStartupStage: null,
            CancellationToken.None);

        Assert.Equal(WorkerAccountRuntimeKind.Multilogin, opened.Runtime);
        Assert.Equal(0, ads.OpenAdsPowerCount);
        Assert.Equal(1, ads.OpenOnConnectedCount);
        Assert.False(ads.LastTrafficMonitoring);
        Assert.Equal(["start", "connect"], mlx.Log);
        await opened.DisposeAsync();
        Assert.Equal(["start", "connect", "stop"], mlx.Log);
        Assert.Equal(0, ads.CloseCount);
    }

    [Fact]
    public async Task Factory_AutomationException_StillStopsMultilogin()
    {
        var ads = new FakeAdsPowerAutomation
        {
            OpenOnConnected = () => throw new InvalidOperationException("automation failed")
        };
        var mlx = new FakeMultiloginConnector();
        var sut = new WorkerAccountSessionFactory(ads, mlx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.OpenAsync(
                MultiloginAccount(),
                new AdsPowerConnectionOptions("http://unused", null),
                null,
                CancellationToken.None));

        Assert.Contains("Multilogin CDP", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AdsPower", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["start", "connect", "stop"], mlx.Log);
    }

    [Fact]
    public async Task Factory_CanceledAfterStart_StillStops()
    {
        using var cts = new CancellationTokenSource();
        var ads = new FakeAdsPowerAutomation();
        var mlx = new FakeMultiloginConnector
        {
            AfterStart = () => cts.Cancel()
        };
        var sut = new WorkerAccountSessionFactory(ads, mlx);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.OpenAsync(
                MultiloginAccount(),
                new AdsPowerConnectionOptions("http://unused", null),
                null,
                cts.Token));

        Assert.Equal(["start", "stop"], mlx.Log);
        Assert.Equal(0, ads.OpenOnConnectedCount);
    }

    [Fact]
    public async Task Factory_LocalMissingUserDataDir_ThrowsRussian()
    {
        var ads = new FakeAdsPowerAutomation();
        var mlx = new FakeMultiloginConnector();
        var chrome = new FakeLocalChromeLauncher();
        var sut = new WorkerAccountSessionFactory(ads, mlx, chrome);
        var account = LocalAccount();
        account.BrowserProfilePath = "";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.OpenAsync(
                account,
                new AdsPowerConnectionOptions("", null),
                null,
                CancellationToken.None));

        Assert.Contains("Обычный браузер", ex.Message, StringComparison.Ordinal);
        Assert.Contains("папке профиля", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, chrome.LaunchCount);
        Assert.Equal(0, ads.OpenAdsPowerCount);
        Assert.Equal(0, mlx.OpenCount);
    }

    [Fact]
    public async Task Factory_LocalPath_LaunchesConnectsThenClosesBrowser()
    {
        var ads = new FakeAdsPowerAutomation();
        var mlx = new FakeMultiloginConnector();
        var chrome = new FakeLocalChromeLauncher();
        var sut = new WorkerAccountSessionFactory(ads, mlx, chrome);

        var opened = await sut.OpenAsync(
            LocalAccount(),
            new AdsPowerConnectionOptions("", null),
            reportStartupStage: null,
            CancellationToken.None);

        Assert.Equal(WorkerAccountRuntimeKind.Local, opened.Runtime);
        Assert.Equal(1, chrome.LaunchCount);
        Assert.Equal("Local", ads.LastRuntimeProvider);
        Assert.True(ads.LastTrafficMonitoring);
        Assert.Equal(1, ads.OpenOnConnectedCount);
        Assert.Equal(0, ads.OpenAdsPowerCount);
        Assert.Equal(0, mlx.OpenCount);
        await opened.DisposeAsync();
        Assert.Equal(1, chrome.LastBrowser!.CloseCount);
        Assert.Equal(1, chrome.LastBrowser.DisposeCount);
        Assert.Equal(0, ads.CloseCount);
        Assert.Equal(0, mlx.StopCount);
    }

    [Fact]
    public async Task Factory_LocalAutomationException_StillClosesBrowser()
    {
        var ads = new FakeAdsPowerAutomation
        {
            OpenOnConnected = () => throw new InvalidOperationException("automation failed")
        };
        var mlx = new FakeMultiloginConnector();
        var chrome = new FakeLocalChromeLauncher();
        var sut = new WorkerAccountSessionFactory(ads, mlx, chrome);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.OpenAsync(
                LocalAccount(),
                new AdsPowerConnectionOptions("", null),
                null,
                CancellationToken.None));

        Assert.Contains("Обычный браузер", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AdsPower", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Multilogin", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, chrome.LaunchCount);
        Assert.Equal(1, chrome.LastBrowser!.CloseCount);
        Assert.Equal(1, chrome.LastBrowser.DisposeCount);
    }

    private static AvitoAccount AdsPowerAccount() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "ads",
        ProfileProvider = AvitoProfileProvider.AdsPower,
        AdsPowerProfileId = "k1ehuqvx",
        AdsPowerApiBaseUrl = "http://local.adspower.net:50325",
        AdsPowerApiKey = "key"
    };

    private static AvitoAccount MultiloginAccount() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "mlx",
        ProfileProvider = AvitoProfileProvider.Multilogin,
        MultiloginProfileId = "profile-mlx",
        MultiloginFolderId = "folder-mlx",
        MultiloginLauncherUrl = "https://launcher.mlx.yt:45001",
        MultiloginAutomationToken = "mlx-secret-token"
    };

    private static AvitoAccount LocalAccount() => new()
    {
        Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
        DisplayName = "chrome-acc",
        ProfileProvider = AvitoProfileProvider.Local,
        BrowserProfilePath = @"D:\Orbita\ChromeProfiles\acc-1",
        LocalChromeExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe"
    };

    private sealed class FakeLocalChromeLauncher : ILocalChromeBrowserLauncher
    {
        public int LaunchCount { get; private set; }

        public LocalChromeLaunchOptions? LastOptions { get; private set; }

        public FakeBrowserProxy? LastBrowser { get; private set; }

        public Task<IBrowser> LaunchAsync(
            LocalChromeLaunchOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaunchCount++;
            LastOptions = options;
            var browser = DispatchProxy.Create<IBrowser, FakeBrowserProxy>();
            LastBrowser = (FakeBrowserProxy)(object)browser;
            return Task.FromResult(browser);
        }
    }

    public class FakeBrowserProxy : DispatchProxy
    {
        public int CloseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool Connected { get; set; } = true;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == "get_IsConnected")
            {
                return Connected;
            }

            if (targetMethod.Name == "CloseAsync")
            {
                CloseCount++;
                Connected = false;
                return Task.CompletedTask;
            }

            if (targetMethod.Name is "Dispose" or "DisposeAsync")
            {
                DisposeCount++;
                if (targetMethod.ReturnType == typeof(ValueTask))
                {
                    return ValueTask.CompletedTask;
                }

                return null;
            }

            if (targetMethod.ReturnType == typeof(Task) || targetMethod.ReturnType.FullName == "System.Threading.Tasks.Task")
            {
                return Task.CompletedTask;
            }

            if (targetMethod.ReturnType.IsGenericType
                && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = targetMethod.ReturnType.GetGenericArguments()[0];
                var result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, [result]);
            }

            if (targetMethod.ReturnType == typeof(ValueTask))
            {
                return ValueTask.CompletedTask;
            }

            if (targetMethod.ReturnType.IsValueType)
            {
                return Activator.CreateInstance(targetMethod.ReturnType);
            }

            return null;
        }
    }

    private sealed class FakeAdsPowerAutomation : IAdsPowerAvitoAutomationService
    {
        public int OpenAdsPowerCount { get; private set; }

        public int OpenOnConnectedCount { get; private set; }

        public int CloseCount { get; private set; }

        public string? LastRuntimeProvider { get; private set; }

        public bool LastTrafficMonitoring { get; private set; }

        public Func<IAdsPowerAccountSession>? OpenOnConnected { get; init; }

        public Task<string> ExtractCandidatesJsonAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default,
            CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null) =>
            throw new NotSupportedException();

        public Task<string> LoadProfileItemsHtmlAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> LoadBlockedItemsHtmlAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> LoadProfileSwitchHtmlAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> CaptureProfileSwitchHtmlInSessionAsync(
            IPage page,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SwitchActiveProfileAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            string subProfileId,
            CancellationToken cancellationToken = default,
            bool closeBrowserAfter = false) =>
            throw new NotSupportedException();

        public Task OpenUrlInRunningProfileAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            string url,
            CancellationToken cancellationToken = default,
            bool closeBrowserAfter = false) =>
            throw new NotSupportedException();

        public Task<IAdsPowerAccountSession> OpenAccountSessionAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default)
        {
            OpenAdsPowerCount++;
            return Task.FromResult<IAdsPowerAccountSession>(new FakeSession());
        }

        public Task CloseBrowserAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default)
        {
            CloseCount++;
            return Task.CompletedTask;
        }

        public Task<IAdsPowerAccountSession> OpenAccountSessionOnConnectedBrowserAsync(
            IBrowser browser,
            string sessionKey,
            Action<string, TimeSpan>? reportStartupStage = null,
            CancellationToken cancellationToken = default,
            string runtimeProvider = "Multilogin",
            LocalChromeTrafficPolicy? trafficPolicy = null)
        {
            _ = browser;
            _ = sessionKey;
            LastRuntimeProvider = runtimeProvider;
            LastTrafficMonitoring = trafficPolicy is { IsMonitoringSession: true };
            OpenOnConnectedCount++;
            if (OpenOnConnected is not null)
            {
                return Task.FromResult(OpenOnConnected());
            }

            return Task.FromResult<IAdsPowerAccountSession>(new FakeSession());
        }
    }

    private sealed class FakeMultiloginConnector : IMultiloginCdpConnector
    {
        public List<string> Log { get; } = [];

        public int OpenCount => Log.Count(static x => x == "start");

        public int StopCount => Log.Count(static x => x == "stop");

        public Action? AfterStart { get; init; }

        public Task<IMultiloginCdpSession> OpenAsync(
            MultiloginConnectionOptions options,
            string folderId,
            string profileId,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            _ = folderId;
            _ = profileId;
            var handedOff = false;
            try
            {
                Log.Add("start");
                AfterStart?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                Log.Add("connect");
                var session = new FakeMlxSession(this);
                handedOff = true;
                return Task.FromResult<IMultiloginCdpSession>(session);
            }
            finally
            {
                if (!handedOff)
                {
                    Log.Add("stop");
                }
            }
        }

        public Task<T> RunAsync<T>(
            MultiloginConnectionOptions options,
            string folderId,
            string profileId,
            Func<IMultiloginConnectedBrowser, CancellationToken, Task<T>> work,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMlxSession(FakeMultiloginConnector owner) : IMultiloginCdpSession
    {
        public IMultiloginConnectedBrowser Connected { get; } = new FakeConnectedBrowser();

        public ValueTask DisposeAsync()
        {
            owner.Log.Add("stop");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeConnectedBrowser : IMultiloginConnectedBrowser
    {
        public bool IsConnected => true;

        public IBrowser Browser { get; } = null!;

        public Task EnsureResponsiveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Disconnect()
        {
        }
    }

    private sealed class FakeSession : IAdsPowerAccountSession
    {
        public string AdsPowerUserId => "x";

        public string? CurrentPageUrl => null;

        public Task<LeadFlow.Core.Services.Avito.SubProfileSwitchResult> SwitchSubProfileAsync(
            string subProfileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifyActiveSubProfileAsync(
            string subProfileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> ExtractCandidatesJsonAsync(
            CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> LoadProfileItemsHtmlAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> LoadBlockedItemsHtmlAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LeadFlow.Core.Services.Avito.AvitoMoneySidebar?> TryReadMoneySidebarAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> CaptureProfileSwitchHtmlAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LeadFlow.Core.Services.Avito.AvitoPageState?> GetPageStateAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]?> CapturePageScreenshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<byte[]?> CapturePageJpegScreenshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<LeadFlow.Core.Services.Browser.BrowserMonitorScreencastCapture> CreateMonitorScreencastCaptureAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using LeadFlow.Core.Models;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Multilogin;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;
using PuppeteerSharp;

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
    }

    [Fact]
    public void Resolve_MultiloginAccount_SelectsMultilogin()
    {
        var account = MultiloginAccount();
        Assert.Equal(WorkerAccountRuntimeKind.Multilogin, WorkerAccountRuntime.Resolve(account));
        Assert.True(WorkerAccountRuntime.IsMultilogin(account));
        Assert.False(WorkerAccountRuntime.IsAdsPower(account));
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

    private sealed class FakeAdsPowerAutomation : IAdsPowerAvitoAutomationService
    {
        public int OpenAdsPowerCount { get; private set; }

        public int OpenOnConnectedCount { get; private set; }

        public int CloseCount { get; private set; }

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
            CancellationToken cancellationToken = default)
        {
            _ = browser;
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

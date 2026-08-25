using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Captcha;
using LeadFlow.Core.Services.Multilogin;
using PuppeteerSharp;

namespace Orbita.Tests;

public sealed class MultiloginCdpConnectorTests
{
    private const string FolderId = "11111111-2222-3333-4444-555555555555";
    private const string ProfileId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Token = "mlx-automation-token";

    [Fact]
    public async Task RunAsync_StartConnectsViaBrowserUrl_ThenStopsOnce()
    {
        var api = new FakeApi();
        var browser = new FakeBrowser();
        var connectorHost = new FakeBrowserConnector { Browser = browser };
        var sut = CreateSut(api, connectorHost);

        var result = await sut.RunAsync(
            ValidOptions(),
            FolderId,
            ProfileId,
            async (connected, _) =>
            {
                Assert.Same(browser, connected);
                await Task.Yield();
                return 42;
            });

        Assert.Equal(42, result);
        Assert.Equal(1, api.StartCount);
        Assert.Equal(1, api.StopCount);
        Assert.Equal(1, connectorHost.ConnectCount);
        Assert.Equal("http://127.0.0.1:35001", connectorHost.LastBrowserUrl);
        Assert.Equal(1, browser.ProbeCount);
        Assert.Equal(1, browser.DisconnectCount);
        Assert.Equal(ProfileId, api.LastStopProfileId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task RunAsync_InvalidPort_DoesNotConnect_AndStillStops(int? port)
    {
        var api = new FakeApi
        {
            StartResult = new MultiloginBrowserStartResult(port, port is null ? null : $"http://127.0.0.1:{port}", null)
        };
        var connectorHost = new FakeBrowserConnector();
        var sut = CreateSut(api, connectorHost);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunAsync(ValidOptions(), FolderId, ProfileId, (_, _) => Task.FromResult(0)));

        Assert.Contains("port", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AdsPower", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, api.StartCount);
        Assert.Equal(1, api.StopCount);
        Assert.Equal(0, connectorHost.ConnectCount);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CdpTimeout_StopsAndUsesMultiloginMessage()
    {
        var api = new FakeApi();
        var connectorHost = new FakeBrowserConnector
        {
            Connect = async (_, _, _, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new FakeBrowser();
            }
        };
        var sut = CreateSut(api, connectorHost, connectTimeout: TimeSpan.FromMilliseconds(40));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            sut.RunAsync(ValidOptions(), FolderId, ProfileId, (_, _) => Task.FromResult(0)));

        Assert.Contains("Multilogin CDP", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AdsPower", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, api.StartCount);
        Assert.Equal(1, api.StopCount);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ConnectFailed_StillStops()
    {
        var api = new FakeApi();
        var workCalled = false;
        var connectorHost = new FakeBrowserConnector
        {
            Connect = (_, _, _, _) => throw new InvalidOperationException("CDP refused")
        };
        var sut = CreateSut(api, connectorHost);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunAsync(ValidOptions(), FolderId, ProfileId, (_, _) =>
            {
                workCalled = true;
                return Task.FromResult(0);
            }));

        Assert.Equal("CDP refused", ex.Message);
        Assert.False(workCalled);
        Assert.Equal(1, api.StartCount);
        Assert.Equal(1, api.StopCount);
        Assert.DoesNotContain("AdsPower", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Success_StopsExactlyOnce()
    {
        var api = new FakeApi();
        var sut = CreateSut(api, new FakeBrowserConnector());

        await sut.RunAsync(ValidOptions(), FolderId, ProfileId, (_, _) => Task.FromResult(true));

        Assert.Equal(1, api.StopCount);
        Assert.Equal(1, api.StartCount);
    }

    [Fact]
    public async Task RunAsync_WorkThrows_StillStopsAndDisconnects()
    {
        var api = new FakeApi();
        var browser = new FakeBrowser();
        var sut = CreateSut(api, new FakeBrowserConnector { Browser = browser });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunAsync<int>(ValidOptions(), FolderId, ProfileId, (_, _) =>
                throw new InvalidOperationException("automation failed")));

        Assert.Equal("automation failed", ex.Message);
        Assert.Equal(1, api.StopCount);
        Assert.Equal(1, browser.DisconnectCount);
        Assert.DoesNotContain("AdsPower", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_StartFailed_DoesNotStop()
    {
        var api = new FakeApi
        {
            StartException = new InvalidOperationException("launcher down")
        };
        var connectorHost = new FakeBrowserConnector();
        var sut = CreateSut(api, connectorHost);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunAsync(ValidOptions(), FolderId, ProfileId, (_, _) => Task.FromResult(0)));

        Assert.Equal(1, api.StartCount);
        Assert.Equal(0, api.StopCount);
        Assert.Equal(0, connectorHost.ConnectCount);
    }

    [Fact]
    public async Task RunAsync_UnresponsiveBrowser_Stops()
    {
        var api = new FakeApi();
        var browser = new FakeBrowser
        {
            Probe = _ => throw new TimeoutException("Multilogin CDP: браузер не отвечает.")
        };
        var workCalled = false;
        var sut = CreateSut(api, new FakeBrowserConnector { Browser = browser });

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            sut.RunAsync(ValidOptions(), FolderId, ProfileId, (_, _) =>
            {
                workCalled = true;
                return Task.FromResult(0);
            }));

        Assert.Contains("браузер не отвечает", ex.Message, StringComparison.Ordinal);
        Assert.False(workCalled);
        Assert.Equal(1, api.StopCount);
        Assert.Equal(1, browser.DisconnectCount);
    }

    [Fact]
    public async Task RunAsync_PassesCancellationTokenToStartAndConnect()
    {
        using var cts = new CancellationTokenSource();
        var api = new FakeApi();
        CancellationToken? connectToken = null;
        var workCalled = false;
        var connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectorHost = new FakeBrowserConnector
        {
            Connect = async (_, _, _, ct) =>
            {
                connectToken = ct;
                connectStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new FakeBrowser();
            }
        };
        var sut = CreateSut(api, connectorHost);
        var runTask = sut.RunAsync(
            ValidOptions(),
            FolderId,
            ProfileId,
            (_, _) =>
            {
                workCalled = true;
                return Task.FromResult(0);
            },
            cts.Token);

        await connectStarted.Task;
        Assert.True(api.LastStartToken.Equals(cts.Token));
        Assert.True(connectToken.HasValue);
        Assert.True(connectToken.Value.CanBeCanceled);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.False(workCalled);
        Assert.Equal(1, api.StartCount);
        Assert.Equal(1, api.StopCount);
    }

    [Fact]
    public async Task RunAsync_ConnectTimeout_WithoutExternalCancellation_StillStops()
    {
        var api = new FakeApi();
        var workCalled = false;
        var connectorHost = new FakeBrowserConnector
        {
            Connect = async (_, _, _, ct) =>
            {
                Assert.True(ct.CanBeCanceled);
                Assert.False(ct.IsCancellationRequested);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new FakeBrowser();
            }
        };
        var sut = CreateSut(api, connectorHost, connectTimeout: TimeSpan.FromMilliseconds(40));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            sut.RunAsync(
                ValidOptions(),
                FolderId,
                ProfileId,
                (_, _) =>
                {
                    workCalled = true;
                    return Task.FromResult(0);
                },
                CancellationToken.None));

        Assert.Contains("Multilogin CDP", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AdsPower", ex.Message, StringComparison.Ordinal);
        Assert.False(workCalled);
        Assert.False(api.LastStartToken.IsCancellationRequested);
        Assert.Equal(1, api.StartCount);
        Assert.Equal(1, api.StopCount);
    }

    [Fact]
    public async Task RunAsync_CanceledAfterStart_StillStops()
    {
        using var cts = new CancellationTokenSource();
        var api = new FakeApi
        {
            AfterStart = () => cts.Cancel()
        };
        var workCalled = false;
        var sut = CreateSut(api, new FakeBrowserConnector
        {
            Connect = (_, _, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<IMultiloginConnectedBrowser>(new FakeBrowser());
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.RunAsync(ValidOptions(), FolderId, ProfileId, (_, _) =>
            {
                workCalled = true;
                return Task.FromResult(0);
            }, cts.Token));

        Assert.False(workCalled);
        Assert.Equal(1, api.StartCount);
        Assert.Equal(1, api.StopCount);
    }

    [Fact]
    public void CreateConnectOptions_UsesBrowserUrl_WithoutWebsocketOrTimeoutProperty()
    {
        var options = PuppeteerMultiloginBrowserConnector.CreateConnectOptions("http://127.0.0.1:35001");

        Assert.Equal("http://127.0.0.1:35001", options.BrowserURL);
        Assert.True(string.IsNullOrEmpty(options.BrowserWSEndpoint));
        Assert.Null(options.DefaultViewport);
        Assert.Null(typeof(ConnectOptions).GetProperty("Timeout"));
    }

    [Fact]
    public void AdsPowerEntryPoints_DoNotAcceptMultiloginTypes()
    {
        var adsPowerTypes = new[]
        {
            typeof(AdsPowerAvitoAutomationService),
            typeof(AdsPowerAvitoAuthService),
            typeof(AdsPowerApiClient),
            typeof(CaptchaSessionHost)
        };

        var parameters = adsPowerTypes
            .SelectMany(static type => type.GetConstructors())
            .SelectMany(static ctor => ctor.GetParameters())
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(parameters, static type =>
            type.Namespace == "LeadFlow.Core.Services.Multilogin");
        Assert.Contains(parameters, static type => type == typeof(IAdsPowerApiClient));
        Assert.DoesNotContain(parameters, static type => type == typeof(IMultiloginApiClient));
        Assert.DoesNotContain(parameters, static type => type == typeof(IMultiloginCdpConnector));
    }

    private static MultiloginCdpConnector CreateSut(
        IMultiloginApiClient api,
        IMultiloginBrowserConnector browserConnector,
        TimeSpan? connectTimeout = null) =>
        new(api, browserConnector, connectTimeout, stopTimeout: TimeSpan.FromSeconds(2));

    private static MultiloginConnectionOptions ValidOptions() => new()
    {
        LauncherUrl = "https://launcher.mlx.yt:45001",
        CloudApiUrl = "https://api.multilogin.com",
        AutomationToken = Token
    };

    private sealed class FakeApi : IMultiloginApiClient
    {
        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public string? LastStopProfileId { get; private set; }

        public CancellationToken LastStartToken { get; private set; }

        public MultiloginBrowserStartResult StartResult { get; init; } =
            MultiloginBrowserStartResult.FromPort(35001);

        public Exception? StartException { get; init; }

        public Action? AfterStart { get; init; }

        public Task<MultiloginBrowserStartResult> StartProfileAsync(
            MultiloginConnectionOptions options,
            string folderId,
            string profileId,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            _ = folderId;
            _ = profileId;
            LastStartToken = cancellationToken;
            StartCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (StartException is not null)
            {
                throw StartException;
            }

            AfterStart?.Invoke();
            return Task.FromResult(StartResult);
        }

        public Task StopProfileAsync(
            MultiloginConnectionOptions options,
            string profileId,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            _ = cancellationToken;
            StopCount++;
            LastStopProfileId = profileId;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBrowserConnector : IMultiloginBrowserConnector
    {
        public int ConnectCount { get; private set; }

        public string? LastBrowserUrl { get; private set; }

        public FakeBrowser Browser { get; init; } = new();

        public Func<string, string?, TimeSpan, CancellationToken, Task<IMultiloginConnectedBrowser>>? Connect
        {
            get;
            init;
        }

        public Task<IMultiloginConnectedBrowser> ConnectAsync(
            string browserUrl,
            string? webSocketDebuggerUrl,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            _ = webSocketDebuggerUrl;
            _ = timeout;
            ConnectCount++;
            LastBrowserUrl = browserUrl;
            if (Connect is not null)
            {
                return Connect(browserUrl, webSocketDebuggerUrl, timeout, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IMultiloginConnectedBrowser>(Browser);
        }
    }

    private sealed class FakeBrowser : IMultiloginConnectedBrowser
    {
        public bool IsConnected { get; init; } = true;

        public IBrowser Browser => null!;

        public int ProbeCount { get; private set; }

        public int DisconnectCount { get; private set; }

        public Func<CancellationToken, Task>? Probe { get; init; }

        public async Task EnsureResponsiveAsync(CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            if (Probe is not null)
            {
                await Probe(cancellationToken).ConfigureAwait(false);
            }

            if (!IsConnected)
            {
                throw new InvalidOperationException("Multilogin CDP: соединение не установлено.");
            }
        }

        public void Disconnect() => DisconnectCount++;
    }
}

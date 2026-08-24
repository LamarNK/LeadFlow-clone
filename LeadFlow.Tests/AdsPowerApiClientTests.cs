using System.Diagnostics;
using System.Net.Http;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Worker;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AdsPowerApiClientTests
{
    [Fact]
    public void SelectExistingAutomationPageIndex_WhenOnlyAnotherAvitoPageIsOpen_UsesItInsteadOfBlankTab()
    {
        var pageIndex = AdsPowerAvitoAutomationService.SelectExistingAutomationPageIndex(
            [
                "https://www.avito.ru/avito-career/vacancies",
                "about:blank"
            ],
            "https://www.avito.ru/profile/pro/items");

        Assert.Equal(0, pageIndex);
    }

    [Fact]
    public void SelectExistingAutomationPageIndex_WhenOnlyAboutBlankIsOpen_ReusesItInsteadOfOpeningNewTab()
    {
        var pageIndex = AdsPowerAvitoAutomationService.SelectExistingAutomationPageIndex(
            ["about:blank"],
            "https://www.avito.ru/profile/pro/items");

        Assert.Equal(0, pageIndex);
    }

    [Fact]
    public void SelectExistingAutomationPageIndex_WhenAdsPowerColonPlaceholderIsOpen_ReusesIt()
    {
        var pageIndex = AdsPowerAvitoAutomationService.SelectExistingAutomationPageIndex(
            [":"],
            "https://www.avito.ru/profile/pro/items");

        Assert.Equal(0, pageIndex);
    }

    [Fact]
    public void SelectExistingAutomationPageIndex_WhenBlankAndChromePages_PrefersBlankOverChrome()
    {
        var pageIndex = AdsPowerAvitoAutomationService.SelectExistingAutomationPageIndex(
            ["chrome://new-tab-page", "about:blank"],
            "https://www.avito.ru/profile/pro/items");

        Assert.Equal(1, pageIndex);
    }

    [Fact]
    public void SelectExistingAutomationPageIndex_WhenNoPages_ReturnsMinusOne()
    {
        var pageIndex = AdsPowerAvitoAutomationService.SelectExistingAutomationPageIndex(
            [],
            "https://www.avito.ru/profile/pro/items");

        Assert.Equal(-1, pageIndex);
    }

    [Fact]
    public void DescribeAutomationPageAcquisitionBranch_EmptyList_IsEmptyPages()
    {
        Assert.Equal("empty_pages", AdsPowerAvitoAutomationService.DescribeAutomationPageAcquisitionBranch(0, -1));
        Assert.Equal("existing_page", AdsPowerAvitoAutomationService.DescribeAutomationPageAcquisitionBranch(1, 0));
        Assert.Equal("existing_page", AdsPowerAvitoAutomationService.DescribeAutomationPageAcquisitionBranch(3, 2));
    }

    [Fact]
    public void CreateEmptyPagesAcquisitionTimeout_HasCdpPrefix_AndIsRetryable()
    {
        var ex = AdsPowerAvitoAutomationService.CreateEmptyPagesAcquisitionTimeout();
        Assert.StartsWith(AdsPowerCdpGuard.TimeoutPrefix, ex.Message, StringComparison.Ordinal);
        Assert.Contains("список вкладок пуст", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NewPage", ex.Message, StringComparison.Ordinal);
        Assert.True(AdsPowerCdpGuard.IsCdpTimeout(ex));
        Assert.True(AdsPowerAvitoAutomationService.IsRetryableAdsPowerStartupFailure(ex));
    }

    [Fact]
    public void EmptyPagesAcquisitionTimeout_WrappedAsSessionFailure_TriggersCdpRetry()
    {
        var inner = AdsPowerAvitoAutomationService.CreateEmptyPagesAcquisitionTimeout();
        var wrapped = new InvalidOperationException(
            $"AdsPower не открыл сессию: последний этап «попытка 2: поиск рабочей вкладки», прошло 12 с. {inner.Message}",
            inner);

        Assert.True(AdsPowerCdpGuard.IsCdpTimeout(wrapped));
        Assert.Same(inner, AdsPowerCdpGuard.FindCdpTimeout(wrapped));
    }

    [Fact]
    public void OuterThreeMinuteStartupTimeout_IsNotCdpRetry()
    {
        var outer = new TimeoutException("AdsPower: запуск сессии не завершился за 3 мин.");
        var wrapped = new InvalidOperationException(
            "AdsPower не открыл сессию: последний этап «попытка 1: поиск рабочей вкладки», прошло 180 с. " + outer.Message,
            outer);

        Assert.False(AdsPowerCdpGuard.IsCdpTimeout(wrapped));
        Assert.Null(AdsPowerCdpGuard.FindCdpTimeout(wrapped));
    }

    [Fact]
    public void ClassifyAutomationPageUrl_MapsKnownClasses()
    {
        Assert.Equal("empty", AdsPowerAvitoAutomationService.ClassifyAutomationPageUrl(null));
        Assert.Equal("blank", AdsPowerAvitoAutomationService.ClassifyAutomationPageUrl("about:blank"));
        Assert.Equal("blank", AdsPowerAvitoAutomationService.ClassifyAutomationPageUrl(":"));
        Assert.Equal("chrome", AdsPowerAvitoAutomationService.ClassifyAutomationPageUrl("chrome://new-tab-page"));
        Assert.Equal("adspower-start", AdsPowerAvitoAutomationService.ClassifyAutomationPageUrl(
            "https://start.adspower.net/?id=k1ehuqvx"));
        Assert.Equal("avito", AdsPowerAvitoAutomationService.ClassifyAutomationPageUrl(
            "https://www.avito.ru/profile/pro/items"));
        Assert.Equal("avito,blank", AdsPowerAvitoAutomationService.FormatAutomationPageUrlClasses(
            ["https://www.avito.ru/profile/pro/items", "about:blank"]));
    }

    [Fact]
    public void ShouldKeepWaitingForStartupNavigation_WhenNoPages_StopsOnWallClockTimeout()
    {
        Assert.True(AdsPowerAvitoAutomationService.ShouldKeepWaitingForStartupNavigation(
            [],
            elapsed: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(5)));
        Assert.False(AdsPowerAvitoAutomationService.ShouldKeepWaitingForStartupNavigation(
            [],
            elapsed: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ResolveAutomationPageAcquisition_EmptyPages_RetriesOnceThenCdpTimeoutWithoutNewPage()
    {
        var events = new List<string>();
        var timestamps = new List<long>();
        var started = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            AdsPowerAvitoAutomationService.ResolveAutomationPageAcquisitionAsync(
                (operation, _) =>
                {
                    events.Add($"poll:{operation}");
                    timestamps.Add(started.ElapsedMilliseconds);
                    return Task.FromResult<IReadOnlyList<string?>>([]);
                },
                "https://www.avito.ru/profile/pro/items",
                CancellationToken.None,
                onEmptyFirstPoll: urls =>
                {
                    Assert.Empty(urls);
                    events.Add("log:empty_pages_retryScheduled");
                    timestamps.Add(started.ElapsedMilliseconds);
                }));

        Assert.Equal(
            [
                $"poll:{AdsPowerAvitoAutomationService.PageAcquireOperation}",
                "log:empty_pages_retryScheduled",
                $"poll:{AdsPowerAvitoAutomationService.PageAcquireRetryOperation}"
            ],
            events);
        Assert.Equal(3, timestamps.Count);
        Assert.True(
            timestamps[2] - timestamps[1] >= MonitoringTiming.AdsPowerStartupNavigationPollMs - 30,
            $"повторный PagesAsync слишком рано после лога: {timestamps[2] - timestamps[1]} мс");
        Assert.StartsWith(AdsPowerCdpGuard.TimeoutPrefix, ex.Message, StringComparison.Ordinal);
        Assert.Contains("NewPage", ex.Message, StringComparison.Ordinal);
        Assert.True(AdsPowerCdpGuard.IsCdpTimeout(ex));
        Assert.True(AdsPowerAvitoAutomationService.IsRetryableAdsPowerStartupFailure(ex));

        var wrapped = new InvalidOperationException(
            $"AdsPower не открыл сессию: последний этап «попытка 2: поиск рабочей вкладки», прошло 12 с. {ex.Message}",
            ex);
        Assert.Same(ex, AdsPowerCdpGuard.FindCdpTimeout(wrapped));

        var night = new DateTime(2026, 8, 24, 20, 30, 0, DateTimeKind.Utc);
        Assert.True(MonitoringNightQuiet.IsActive(night));
        var retry = WorkerAccountPassDelay.Resolve(
            retryAfter: TimeSpan.FromMinutes(1),
            polled: true,
            newResponses: 0,
            quietStreak: 4,
            backlog: false,
            historicalHeat: 0,
            utcNow: night);
        Assert.Equal(TimeSpan.FromMinutes(1), retry);
    }

    [Fact]
    public async Task ResolveAutomationPageAcquisition_EmptyThenExisting_LogsEmptyPagesBeforeRetry()
    {
        var events = new List<string>();
        var result = await AdsPowerAvitoAutomationService.ResolveAutomationPageAcquisitionAsync(
            (operation, _) =>
            {
                events.Add($"poll:{operation}");
                IReadOnlyList<string?> urls = events.Count(static e => e.StartsWith("poll:", StringComparison.Ordinal)) == 1
                    ? []
                    : ["https://www.avito.ru/profile/pro/items"];
                return Task.FromResult(urls);
            },
            "https://www.avito.ru/profile/pro/items",
            CancellationToken.None,
            onEmptyFirstPoll: urls =>
            {
                Assert.Empty(urls);
                events.Add("log:empty_pages_retryScheduled");
            });

        Assert.Equal(
            [
                $"poll:{AdsPowerAvitoAutomationService.PageAcquireOperation}",
                "log:empty_pages_retryScheduled",
                $"poll:{AdsPowerAvitoAutomationService.PageAcquireRetryOperation}"
            ],
            events);
        Assert.Equal("existing_page", result.Branch);
        Assert.Equal(0, result.SelectedIndex);
    }

    [Fact]
    public async Task ResolveAutomationPageAcquisition_ExistingOnFirstPoll_DoesNotRetry()
    {
        var operations = new List<string>();
        var started = Stopwatch.StartNew();
        var result = await AdsPowerAvitoAutomationService.ResolveAutomationPageAcquisitionAsync(
            (operation, _) =>
            {
                operations.Add(operation);
                return Task.FromResult<IReadOnlyList<string?>>(["about:blank"]);
            },
            "https://www.avito.ru/profile/pro/items",
            CancellationToken.None,
            onEmptyFirstPoll: _ => throw new InvalidOperationException(
                "пустой PagesAsync не должен логироваться, если первая выборка уже не пустая"));

        Assert.Equal([AdsPowerAvitoAutomationService.PageAcquireOperation], operations);
        Assert.Equal("existing_page", result.Branch);
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void SelectExistingAutomationPageIndex_WhenOnlyChromeNewTab_ReusesItInsteadOfOpeningNewTab()
    {
        var pageIndex = AdsPowerAvitoAutomationService.SelectExistingAutomationPageIndex(
            ["chrome://new-tab-page"],
            "https://www.avito.ru/profile/pro/items");

        Assert.Equal(0, pageIndex);
    }

    [Fact]
    public void SelectExistingAutomationPageIndex_WhenOnlyUnknownPage_ReusesItInsteadOfOpeningNewTab()
    {
        var pageIndex = AdsPowerAvitoAutomationService.SelectExistingAutomationPageIndex(
            ["chrome://settings"],
            "https://www.avito.ru/profile/pro/items");

        Assert.Equal(0, pageIndex);
    }

    [Fact]
    public void ShouldCloseNonWorkerPages_OnlyAfterWorkerLeftPlaceholder()
    {
        Assert.False(AdsPowerAvitoAutomationService.ShouldCloseNonWorkerPages("about:blank"));
        Assert.False(AdsPowerAvitoAutomationService.ShouldCloseNonWorkerPages("chrome://new-tab-page"));
        Assert.True(AdsPowerAvitoAutomationService.ShouldCloseNonWorkerPages(
            "https://www.avito.ru/profile/pro/items"));
    }

    [Fact]
    public void IsRetryableAdsPowerStartupFailure_RetriesBlankTabButNotLimits()
    {
        Assert.True(AdsPowerAvitoAutomationService.IsRetryableAdsPowerStartupFailure(
            new InvalidOperationException("AdsPower: вкладка осталась на «about:blank», страница Avito не открылась.")));
        Assert.False(AdsPowerAvitoAutomationService.IsRetryableAdsPowerStartupFailure(
            new AdsPowerProfileInUseException(-1, "in use")));
        Assert.False(AdsPowerAvitoAutomationService.IsRetryableAdsPowerStartupFailure(
            new AdsPowerProxyFailureException("https://start.adspower.net/?id=k1dp9we7")));
        Assert.False(AdsPowerAvitoAutomationService.IsRetryableAdsPowerStartupFailure(
            new OperationCanceledException()));
        Assert.True(AdsPowerAvitoAutomationService.IsRetryableAdsPowerStartupFailure(
            new AdsPowerLocalApiTimeoutException("user/list", "queue_wait", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))));
    }

    [Theory]
    [InlineData(null, 0, 1)]
    [InlineData("about:blank", 1, 2)]
    [InlineData(":", 2, 4)]
    public void NextStartupNavigationStep_WhenStillOnPlaceholder_NavigatesExistingTabInsteadOfCreatingOne(
        string? url,
        int lastAttempt,
        int expected)
    {
        Assert.Equal(
            (AdsPowerAvitoAutomationService.AdsPowerStartupNavigationStep)expected,
            AdsPowerAvitoAutomationService.NextStartupNavigationStep(
                url,
                (AdsPowerAvitoAutomationService.AdsPowerStartupNavigationStep)lastAttempt));
    }

    [Fact]
    public void NextStartupNavigationStep_WhenAvitoAlreadyOpen_IsDone()
    {
        Assert.Equal(
            AdsPowerAvitoAutomationService.AdsPowerStartupNavigationStep.Done,
            AdsPowerAvitoAutomationService.NextStartupNavigationStep(
                "https://www.avito.ru/profile/pro/items",
                AdsPowerAvitoAutomationService.AdsPowerStartupNavigationStep.None));
    }

    [Fact]
    public void ShouldKeepWaitingForStartupNavigation_WhileOnlyBlank_UntilTimeout()
    {
        Assert.True(AdsPowerAvitoAutomationService.ShouldKeepWaitingForStartupNavigation(
            ["about:blank"],
            elapsed: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(8)));

        Assert.False(AdsPowerAvitoAutomationService.ShouldKeepWaitingForStartupNavigation(
            ["https://www.avito.ru/profile/pro/items"],
            elapsed: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(8)));

        Assert.False(AdsPowerAvitoAutomationService.ShouldKeepWaitingForStartupNavigation(
            ["about:blank"],
            elapsed: TimeSpan.FromSeconds(8),
            timeout: TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public async Task OpenAccountSessionAsync_StartsAdsPowerOnProfileItemsPage()
    {
        var apiClient = new RecordingAdsPowerApiClient();
        var service = new AdsPowerAvitoAutomationService(apiClient, null!, null!);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.OpenAccountSessionAsync(
                new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
                "user-1",
                CancellationToken.None));

        Assert.Equal("https://www.avito.ru/profile/pro/items", apiClient.OpenUrl);
    }

    [Fact]
    public async Task ListProfilesAsync_UsesBearerAuthorizationHeader_WhenApiKeyProvided()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = BuildClient((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(StubHttpMessageHandler.Ok("""{"code":0,"data":{"list":[]}}"""));
        });

        await client.ListProfilesAsync(
            new AdsPowerConnectionOptions("http://127.0.0.1:57610", "secret-key"),
            CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("secret-key", capturedRequest.Headers.Authorization?.Parameter);
        Assert.False(capturedRequest.Headers.Contains("Api-Key"));
    }

    [Fact]
    public async Task ListProfilesAsync_PassesGroupId_AndParsesGroupFields()
    {
        HttpRequestMessage? capturedRequest = null;
        var body = """
            {"code":0,"data":{"list":[
              {"user_id":"u1","name":"Acc","serial_number":"12","group_id":1001,"group_name":"Orbita"}
            ]}}
            """;
        var client = BuildClient((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(StubHttpMessageHandler.Ok(body));
        });

        var profiles = await client.ListProfilesAsync(
            new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
            CancellationToken.None,
            groupId: "1001");

        Assert.NotNull(capturedRequest);
        Assert.Equal("/api/v1/user/list", capturedRequest!.RequestUri?.AbsolutePath);
        var query = Uri.UnescapeDataString(capturedRequest.RequestUri?.Query ?? string.Empty);
        Assert.Contains("group_id=1001", query);
        Assert.Contains("page=1", query);
        Assert.Contains("page_size=100", query);
        var profile = Assert.Single(profiles);
        Assert.Equal("u1", profile.UserId);
        Assert.Equal("Acc", profile.Name);
        Assert.Equal("12", profile.SerialNumber);
        Assert.Equal("1001", profile.GroupId);
        Assert.Equal("Orbita", profile.GroupName);
    }

    [Fact]
    public async Task GetProfileProxyAsync_UsesProfileIdAndReturnsCredentials()
    {
        HttpRequestMessage? capturedRequest = null;
        var body = """
            {"code":0,"data":{"list":[{
              "user_id":"profile-1",
              "user_proxy_config":{
                "proxy_type":"socks5","proxy_host":"203.0.113.10","proxy_port":"1080",
                "proxy_user":"login","proxy_password":"secret"
              }
            }]}}
            """;
        var client = BuildClient((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(StubHttpMessageHandler.Ok(body));
        });

        // До регрессии интерфейс AdsPower вообще не позволял прочитать proxy профиля:
        // воркер всегда отправлял RuCaptchaTask как proxyless.
        var method = typeof(AdsPowerApiClient).GetMethod("GetProfileProxyAsync");
        Assert.NotNull(method);
        var pending = Assert.IsAssignableFrom<Task>(method!.Invoke(client,
            [new AdsPowerConnectionOptions("http://127.0.0.1:57610", null), "profile-1", CancellationToken.None]));
        await pending;

        Assert.NotNull(capturedRequest);
        Assert.Equal("/api/v1/user/list", capturedRequest!.RequestUri?.AbsolutePath);
        var query = Uri.UnescapeDataString(capturedRequest.RequestUri?.Query ?? string.Empty);
        Assert.Contains("user_id=[\"profile-1\"]", query, StringComparison.Ordinal);
        Assert.Contains("page_size=1", query);

        var result = pending.GetType().GetProperty("Result")?.GetValue(pending);
        Assert.NotNull(result);
        Assert.Equal("socks5", result!.GetType().GetProperty("Type")?.GetValue(result));
        Assert.Equal("203.0.113.10:1080", result.GetType().GetProperty("Address")?.GetValue(result));
        Assert.Equal("login", result.GetType().GetProperty("Username")?.GetValue(result));
    }

    [Fact]
    public async Task ListGroupsAsync_ParsesGroupIdAndName()
    {
        HttpRequestMessage? capturedRequest = null;
        var body = """
            {"code":0,"data":{"list":[
              {"group_id":"0","group_name":"Ungrouped"},
              {"group_id":1001,"group_name":"Orbita"}
            ]}}
            """;
        var client = BuildClient((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(StubHttpMessageHandler.Ok(body));
        });

        var groups = await client.ListGroupsAsync(
            new AdsPowerConnectionOptions("http://127.0.0.1:57610", "secret-key"),
            CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("/api/v1/group/list", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal(2, groups.Count);
        Assert.Equal("0", groups[0].GroupId);
        Assert.Equal("Ungrouped", groups[0].GroupName);
        Assert.Equal("1001", groups[1].GroupId);
        Assert.Equal("Orbita", groups[1].GroupName);
    }

    [Fact]
    public async Task StartBrowserAsync_UsesBearerAuthorizationHeader_AndEncodesOpenUrl()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = BuildClient((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(StubHttpMessageHandler.Ok("""{"code":0,"data":{}}"""));
        });

        await client.StartBrowserAsync(
            new AdsPowerConnectionOptions("http://127.0.0.1:57610/", "secret-key"),
            "user-1",
            "https://www.avito.ru/profile",
            CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("secret-key", capturedRequest.Headers.Authorization?.Parameter);
        Assert.Equal("/api/v1/browser/start", capturedRequest.RequestUri?.AbsolutePath);

        var decodedQuery = Uri.UnescapeDataString(capturedRequest.RequestUri?.Query ?? string.Empty);
        Assert.Contains("user_id=user-1", decodedQuery);
        Assert.Contains("""open_urls=["https://www.avito.ru/profile"]""", decodedQuery);
        Assert.Contains("ip_tab=0", decodedQuery);
        Assert.DoesNotContain("open_tabs=", decodedQuery);
    }

    [Fact]
    public async Task StopBrowserAsync_UsesBearerAuthorizationHeader_AndEncodesUserId()
    {
        HttpRequestMessage? capturedRequest = null;
        var client = BuildClient((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(StubHttpMessageHandler.Ok("""{"code":0,"msg":"success"}"""));
        });

        await client.StopBrowserAsync(
            new AdsPowerConnectionOptions("http://127.0.0.1:57610/", "secret-key"),
            "user-1",
            CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("secret-key", capturedRequest.Headers.Authorization?.Parameter);
        Assert.Equal("/api/v1/browser/stop", capturedRequest.RequestUri?.AbsolutePath);

        var decodedQuery = Uri.UnescapeDataString(capturedRequest.RequestUri?.Query ?? string.Empty);
        Assert.Contains("user_id=user-1", decodedQuery);
    }

    [Fact]
    public async Task StartBrowserAsync_ThrowsAdsPowerDailyOpenLimitExceededException_WhenApiReturnsDailyLimit()
    {
        var body = """{"code":-1,"msg":"Exceeding open daily limit, recovery after 7 hours"}""";
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(body)));

        var ex = await Assert.ThrowsAsync<AdsPowerDailyOpenLimitExceededException>(() =>
            client.StartBrowserAsync(
                new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
                "user-1",
                null,
                CancellationToken.None));

        Assert.Equal(-1, ex.ApiCode);
        Assert.Contains("daily limit", ex.ApiMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartBrowserAsync_RetriesOnRateLimit_AndSucceedsOnSecondAttempt()
    {
        var attempts = 0;
        var rateLimitBody = """{"code":-1,"msg":"Too many request per second, please check"}""";
        var successBody = """{"code":0,"data":{"ws":{"puppeteer":"ws://127.0.0.1:9222/devtools/browser/abc"}}}""";
        var client = BuildClient((_, _) =>
        {
            attempts++;
            return Task.FromResult(StubHttpMessageHandler.Ok(attempts == 1 ? rateLimitBody : successBody));
        });

        var result = await client.StartBrowserAsync(
            new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
            "user-1",
            null,
            CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal("ws://127.0.0.1:9222/devtools/browser/abc", result.WebSocketDebuggerUrl);
    }

    [Fact]
    public async Task StartBrowserAsync_ThrowsAdsPowerProfileInUseException_WhenProfileAlreadyOpen()
    {
        var body =
            """{"code":-1,"msg":"[k1cu2550] is being used by [a.pakin797@gmail.com] and is not allowed to open"}""";
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(body)));

        var ex = await Assert.ThrowsAsync<AdsPowerProfileInUseException>(() =>
            client.StartBrowserAsync(
                new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
                "user-1",
                null,
                CancellationToken.None));

        Assert.Equal(-1, ex.ApiCode);
        Assert.Contains("k1cu2550", ex.UserMessage);
        Assert.DoesNotContain("a.pakin797@gmail.com", ex.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("***@***", ex.UserMessage);
    }

    [Fact]
    public async Task StartBrowserAsync_ThrowsAdsPowerRateLimitExceededException_WhenRateLimitPersists()
    {
        var body = """{"code":-1,"msg":"Too many request per second, please check"}""";
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(body)));

        var ex = await Assert.ThrowsAsync<AdsPowerRateLimitExceededException>(() =>
            client.StartBrowserAsync(
                new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
                "user-1",
                null,
                CancellationToken.None));

        Assert.Equal(-1, ex.ApiCode);
        Assert.Contains("Too many request", ex.ApiMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static AdsPowerApiClient BuildClient(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> handler)
    {
        var stub = new StubHttpMessageHandler(handler);
        var factory = new StubHttpClientFactory(stub);
        return new AdsPowerApiClient(factory);
    }

    private sealed class RecordingAdsPowerApiClient : IAdsPowerApiClient
    {
        public string? OpenUrl { get; private set; }

        public Task<IReadOnlyList<AdsPowerProfileSummary>> ListProfilesAsync(
            AdsPowerConnectionOptions options,
            CancellationToken cancellationToken = default,
            string? groupId = null) =>
            Task.FromResult<IReadOnlyList<AdsPowerProfileSummary>>([]);

        public Task<AdsPowerProfileProxy?> GetProfileProxyAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AdsPowerProfileProxy?>(null);

        public Task<IReadOnlyList<AdsPowerGroupSummary>> ListGroupsAsync(
            AdsPowerConnectionOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AdsPowerGroupSummary>>([]);

        public Task<AdsPowerBrowserStartResult> StartBrowserAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            string? openUrl,
            CancellationToken cancellationToken = default)
        {
            OpenUrl = openUrl;
            return Task.FromResult(new AdsPowerBrowserStartResult(null, null));
        }

        public Task StopBrowserAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

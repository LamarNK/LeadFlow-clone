using System.Net.Http;
using LeadFlow.Core.Services.AdsPower;
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
    public void SelectExistingAutomationPageIndex_WhenOnlyChromeInternalPage_DoesNotReuseIt()
    {
        var pageIndex = AdsPowerAvitoAutomationService.SelectExistingAutomationPageIndex(
            ["chrome://new-tab-page"],
            "https://www.avito.ru/profile/pro/items");

        Assert.Equal(-1, pageIndex);
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
        Assert.Contains("a.pakin797@gmail.com", ex.UserMessage);
        Assert.Contains("k1cu2550", ex.UserMessage);
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AdsPowerProfileSummary>>([]);

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

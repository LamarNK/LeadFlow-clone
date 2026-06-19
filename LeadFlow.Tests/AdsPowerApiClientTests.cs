using System.Net.Http;
using LeadFlow.Services.AdsPower;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AdsPowerApiClientTests
{
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
}

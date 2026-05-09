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

    private static AdsPowerApiClient BuildClient(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> handler)
    {
        var stub = new StubHttpMessageHandler(handler);
        var factory = new StubHttpClientFactory(stub);
        return new AdsPowerApiClient(factory);
    }
}

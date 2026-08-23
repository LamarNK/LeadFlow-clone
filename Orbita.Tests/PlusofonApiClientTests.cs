using System.Net;
using System.Text;
using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class PlusofonApiClientTests
{
    [Fact]
    public async Task GetRecordingAsync_SendsOfficialHeadersAndReadsHttpsRecord()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"success\":true,\"record\":\"https://records.plusofon.test/call-42.mp3\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var sut = new PlusofonApiClient(new StubFactory(handler));

        var result = await sut.GetRecordingAsync("client-1", "secret-token", "call-42");

        Assert.Equal(PlusofonRecordingOutcome.Ready, result.Outcome);
        Assert.Equal("https://records.plusofon.test/call-42.mp3", result.RecordingUrl);
        Assert.NotNull(captured);
        Assert.Equal(
            "https://restapi.plusofon.ru/api/v1/call/call-42/record",
            captured.RequestUri?.AbsoluteUri);
        Assert.Equal("client-1", Assert.Single(captured.Headers.GetValues("Client")));
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", captured.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task GetRecordingAsync_WhenRecordIsNotReady_ReturnsRetryableOutcome()
    {
        var sut = new PlusofonApiClient(new StubFactory(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound))));

        var result = await sut.GetRecordingAsync("client-1", "secret-token", "call-42");

        Assert.Equal(PlusofonRecordingOutcome.NotReady, result.Outcome);
        Assert.Null(result.RecordingUrl);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://restapi.plusofon.ru/")
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}

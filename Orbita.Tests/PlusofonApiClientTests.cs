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

    [Fact]
    public async Task GetCallsAsync_ReadsWholeAccountPageAndRecording()
    {
        HttpRequestMessage? captured = null;
        var sut = new PlusofonApiClient(new StubFactory(new StubHandler(request =>
        {
            captured = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "current_page": 1,
                      "last_page": 1,
                      "data": [{
                        "call_id": "call-100",
                        "number_a": "202",
                        "number_b": "79991112233",
                        "cld": "79991112233",
                        "direction": "external",
                        "account": "202",
                        "connect_time": "2026-08-29 10:00:00",
                        "duration": 75,
                        "record": "https://rec.plusofon.ru/call-100.mp3"
                      }]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        })));

        var result = await sut.GetCallsAsync(
            "client-1",
            "secret-token",
            new DateTime(2026, 8, 29, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            1);

        Assert.Equal(PlusofonRecordingOutcome.Ready, result.Outcome);
        Assert.False(result.HasMore);
        var call = Assert.Single(result.Calls);
        Assert.Equal("call-100", call.CallId);
        Assert.Equal("202", call.ProviderUserKey);
        Assert.Equal("https://rec.plusofon.ru/call-100.mp3", call.RecordingUrl);
        Assert.Contains("api/v1/call?", captured?.RequestUri?.AbsoluteUri);
        Assert.Equal("client-1", Assert.Single(captured!.Headers.GetValues("Client")));
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

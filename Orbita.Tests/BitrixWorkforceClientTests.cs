using System.Net;
using System.Text;
using System.Text.Json;
using Orbita.Api.Services.Bitrix;

namespace Orbita.Tests;

public sealed class BitrixWorkforceClientTests
{
    [Theory]
    [InlineData("""{"result":false}""")]
    [InlineData("""{"result":null}""")]
    [InlineData("""{"result":{}}""")]
    public async Task UpdateDealAsync_RejectsResponseThatDoesNotConfirmUpdate(
        string responseBody)
    {
        var sut = CreateClient(responseBody);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateDealAsync(
                "https://example.bitrix24.ru/rest/1/secret",
                123,
                new Dictionary<string, object?> { ["ASSIGNED_BY_ID"] = 10 },
                CancellationToken.None));

        Assert.Contains("did not confirm", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateContactOwnerAsync_AcceptsConfirmedUpdate()
    {
        var sut = CreateClient("""{"result":true}""");

        await sut.UpdateContactOwnerAsync(
            "https://example.bitrix24.ru/rest/1/secret",
            456,
            10,
            CancellationToken.None);
    }

    [Fact]
    public async Task ListDealsAsync_FollowsPaginationAndDeduplicatesDealsAcrossStages()
    {
        var starts = new List<int>();
        var call = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            using var requestJson = JsonDocument.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            starts.Add(requestJson.RootElement.GetProperty("start").GetInt32());
            call++;
            var responseBody = call switch
            {
                1 =>
                    """
                    {
                      "result": [
                        { "ID": "1", "STAGE_ID": "A", "DATE_MODIFY": "r1" },
                        { "ID": "2", "STAGE_ID": "A", "DATE_MODIFY": "r2" }
                      ],
                      "next": 50
                    }
                    """,
                2 =>
                    """
                    {
                      "result": [
                        { "ID": "2", "STAGE_ID": "A", "DATE_MODIFY": "r2" },
                        { "ID": "3", "STAGE_ID": "A", "DATE_MODIFY": "r3" }
                      ]
                    }
                    """,
                3 =>
                    """
                    {
                      "result": [
                        { "ID": "3", "STAGE_ID": "B", "DATE_MODIFY": "r3b" },
                        { "ID": "4", "STAGE_ID": "B", "DATE_MODIFY": "r4" }
                      ]
                    }
                    """,
                _ => throw new InvalidOperationException("Unexpected Bitrix24 page request.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        });
        var sut = new BitrixWorkforceClient(new StubHttpClientFactory(handler));

        var deals = await sut.ListDealsAsync(
            "https://example.bitrix24.ru/rest/1/secret",
            0,
            ["A", "B"],
            CancellationToken.None);

        Assert.Equal([0, 50, 0], starts);
        Assert.Equal([1L, 2L, 3L, 4L], deals.Select(x => x.DealId));
        Assert.Equal("A", deals.Single(x => x.DealId == 3).StageId);
    }

    private static BitrixWorkforceClient CreateClient(string responseBody)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        });
        return new BitrixWorkforceClient(new StubHttpClientFactory(handler));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}

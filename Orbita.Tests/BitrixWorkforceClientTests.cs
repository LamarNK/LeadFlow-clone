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
    public async Task ListDealsAsync_RejectsDealThatMovesAcrossStagesDuringSnapshot()
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

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ListDealsAsync(
                "https://example.bitrix24.ru/rest/1/secret",
                0,
                ["A", "B"],
                CancellationToken.None));

        Assert.Equal([0, 50, 0], starts);
        Assert.Contains("3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateDealFieldsAsync_AcceptsAllConfiguredFieldsCaseInsensitively()
    {
        var sut = CreateClient(
            """
            {
              "result": {
                "ID": { "type": "integer" },
                "UF_AGE": { "type": "integer" },
                "UF_CITY": { "type": "string" }
              }
            }
            """);

        await sut.ValidateDealFieldsAsync(
            "https://example.bitrix24.ru/rest/1/secret",
            ["uf_age", "UF_CITY", "UF_AGE", " "],
            CancellationToken.None);
    }

    [Fact]
    public async Task GetContactOwnerIdAsync_ReturnsCurrentResponsible()
    {
        var sut = CreateClient("""{"result":{"ID":"456","ASSIGNED_BY_ID":"27"}}""");

        var ownerId = await sut.GetContactOwnerIdAsync(
            "https://example.bitrix24.ru/rest/1/secret",
            456,
            CancellationToken.None);

        Assert.Equal(27L, ownerId);
    }

    [Fact]
    public async Task ValidateDealFieldsAsync_RejectsUnknownConfiguredField()
    {
        var sut = CreateClient(
            """{"result":{"ID":{"type":"integer"},"UF_AGE":{"type":"integer"}}}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ValidateDealFieldsAsync(
                "https://example.bitrix24.ru/rest/1/secret",
                ["UF_AGE", "UF_MISSING"],
                CancellationToken.None));

        Assert.Contains("UF_MISSING", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "DEAL_STAGE", "NEW", "IN_PROCESS")]
    [InlineData(7, "DEAL_STAGE_7", "C7:NEW", "C7:IN_PROCESS")]
    public async Task ValidateDealPipelineAsync_ValidatesCategoryAndConfiguredStages(
        int categoryId,
        string expectedStageEntityId,
        string sourceStageId,
        string targetStageId)
    {
        var methods = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            using var body = JsonDocument.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/crm.category.list.json", StringComparison.Ordinal))
            {
                methods.Add("crm.category.list");
                Assert.Equal(2, body.RootElement.GetProperty("entityTypeId").GetInt32());
                Assert.Equal(0, body.RootElement.GetProperty("start").GetInt32());
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    result = new
                    {
                        categories = new[] { new { id = categoryId, name = "Candidates" } }
                    }
                }));
            }

            Assert.EndsWith("/crm.status.list.json", path, StringComparison.Ordinal);
            methods.Add("crm.status.list");
            Assert.Equal(
                expectedStageEntityId,
                body.RootElement
                    .GetProperty("filter")
                    .GetProperty("ENTITY_ID")
                    .GetString());
            return JsonResponse(JsonSerializer.Serialize(new
            {
                result = new[]
                {
                    new { STATUS_ID = sourceStageId },
                    new { STATUS_ID = targetStageId }
                }
            }));
        });
        var sut = new BitrixWorkforceClient(new StubHttpClientFactory(handler));

        await sut.ValidateDealPipelineAsync(
            "https://example.bitrix24.ru/rest/1/secret",
            categoryId,
            [sourceStageId, targetStageId, sourceStageId.ToLowerInvariant()],
            CancellationToken.None);

        Assert.Equal(["crm.category.list", "crm.status.list"], methods);
    }

    [Fact]
    public async Task ValidateDealPipelineAsync_RejectsUnknownConfiguredStage()
    {
        var call = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            call++;
            return call switch
            {
                1 => JsonResponse(
                    """{"result":{"categories":[{"id":0,"name":"Main"}]}}"""),
                2 => JsonResponse(
                    """{"result":[{"STATUS_ID":"NEW"}]}"""),
                _ => throw new InvalidOperationException("Unexpected Bitrix24 request.")
            };
        });
        var sut = new BitrixWorkforceClient(new StubHttpClientFactory(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ValidateDealPipelineAsync(
                "https://example.bitrix24.ru/rest/1/secret",
                0,
                ["NEW", "MISSING"],
                CancellationToken.None));

        Assert.Contains("MISSING", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateDealPipelineAsync_RejectsUnknownOrInaccessibleCategory()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            calls++;
            return JsonResponse(
                """{"result":{"categories":[{"id":0,"name":"Main"}]}}""");
        });
        var sut = new BitrixWorkforceClient(new StubHttpClientFactory(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ValidateDealPipelineAsync(
                "https://example.bitrix24.ru/rest/1/secret",
                7,
                ["C7:NEW"],
                CancellationToken.None));

        Assert.Contains("category 7", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, calls);
    }

    private static BitrixWorkforceClient CreateClient(string responseBody)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        });
        return new BitrixWorkforceClient(new StubHttpClientFactory(handler));
    }

    private static HttpResponseMessage JsonResponse(string responseBody) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };

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

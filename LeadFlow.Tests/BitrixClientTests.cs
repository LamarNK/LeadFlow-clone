using System.Net;
using System.Net.Http;
using System.Text.Json;
using LeadFlow.Models;
using LeadFlow.Services;
using LeadFlow.Services.Bitrix;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class BitrixClientTests
{
    private const string Webhook = "https://b24-test.bitrix24.ru/rest/1/abc/";

    [Fact]
    public async Task HasDuplicate_EmptyWebhook_ReturnsSkipped()
    {
        var client = BuildClient((_, _) => throw new InvalidOperationException("Не должно быть HTTP-вызовов"));
        var settings = NewSettings(webhook: string.Empty);

        var result = await client.HasDuplicateAsync("79000000000", settings, CancellationToken.None);

        Assert.True(result.IsSkipped);
    }

    [Fact]
    public async Task HasDuplicate_EmptyPhone_ReturnsSkipped()
    {
        var client = BuildClient((_, _) => throw new InvalidOperationException("Не должно быть HTTP-вызовов"));

        var result = await client.HasDuplicateAsync(string.Empty, NewSettings(), CancellationToken.None);

        Assert.True(result.IsSkipped);
    }

    [Fact]
    public async Task HasDuplicate_HttpInternalServerError_ReturnsUnavailable()
    {
        var client = BuildClient((_, _) =>
            Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, """{"error":"server"}""")));

        var result = await client.HasDuplicateAsync("79000000000", NewSettings(), CancellationToken.None);

        Assert.True(result.IsUnavailable);
        Assert.Contains("500", result.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public async Task HasDuplicate_BitrixApiError_ReturnsUnavailable()
    {
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(
            """{"error":"INVALID_CREDENTIALS","error_description":"Неверный токен"}""")));

        var result = await client.HasDuplicateAsync("79000000000", NewSettings(), CancellationToken.None);

        Assert.True(result.IsUnavailable);
        Assert.Contains("INVALID_CREDENTIALS", result.ErrorMessage ?? string.Empty);
        Assert.Contains("Неверный токен", result.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public async Task HasDuplicate_NonEmptyContactArray_ReturnsDuplicate()
    {
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(
            """{"result":{"CONTACT":["1","2"]}}""")));

        var result = await client.HasDuplicateAsync("79000000000", NewSettings(), CancellationToken.None);

        Assert.Equal(BitrixDuplicateLookupOutcome.Duplicate, result.Outcome);
    }

    [Fact]
    public async Task HasDuplicate_EmptyContactArray_ReturnsNoDuplicate()
    {
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(
            """{"result":{"CONTACT":[]}}""")));

        var result = await client.HasDuplicateAsync("79000000000", NewSettings(), CancellationToken.None);

        Assert.Equal(BitrixDuplicateLookupOutcome.NoDuplicate, result.Outcome);
    }

    [Fact]
    public async Task HasDuplicate_RequestBodyContainsBothPhoneVariants()
    {
        string? capturedBody = null;
        var client = BuildClient((_, body) =>
        {
            capturedBody = body;
            return Task.FromResult(StubHttpMessageHandler.Ok("""{"result":{"CONTACT":[]}}"""));
        });

        await client.HasDuplicateAsync("79000000000", NewSettings(), CancellationToken.None);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var values = doc.RootElement.GetProperty("values").EnumerateArray()
            .Select(x => x.GetString())
            .ToHashSet();
        Assert.Contains("79000000000", values);
        Assert.Contains("+79000000000", values);
    }

    [Fact]
    public async Task CreateLead_HappyPath_CreatesContactThenDeal()
    {
        var (handler, client) = BuildClientWithHandler((req, _) =>
        {
            return req.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/crm.contact.add.json", StringComparison.Ordinal)
                    => Task.FromResult(StubHttpMessageHandler.Ok("""{"result":42}""")),
                var p when p.EndsWith("/crm.deal.add.json", StringComparison.Ordinal)
                    => Task.FromResult(StubHttpMessageHandler.Ok("""{"result":777}""")),
                _ => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))
            };
        });

        var response = NewResponse();
        var result = await client.CreateLeadAsync(response, NewSettings(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("777", result.EntityId);
        Assert.Equal("42", result.ContactId);
        Assert.Equal(2, handler.Calls.Count);
        Assert.EndsWith("/crm.contact.add.json", handler.Calls[0].Path);
        Assert.EndsWith("/crm.deal.add.json", handler.Calls[1].Path);
    }

    [Fact]
    public async Task CreateLead_DealFails_DeletesOrphanContact_AndReturnsFailureWithoutContactId()
    {
        var (handler, client) = BuildClientWithHandler((req, _) =>
        {
            return req.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/crm.contact.add.json", StringComparison.Ordinal)
                    => Task.FromResult(StubHttpMessageHandler.Ok("""{"result":42}""")),
                var p when p.EndsWith("/crm.deal.add.json", StringComparison.Ordinal)
                    => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{}")),
                var p when p.EndsWith("/crm.contact.delete.json", StringComparison.Ordinal)
                    => Task.FromResult(StubHttpMessageHandler.Ok("""{"result":true}""")),
                _ => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))
            };
        });

        var result = await client.CreateLeadAsync(NewResponse(), NewSettings(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(string.Empty, result.ContactId);
        Assert.Contains(handler.Calls, c => c.Path.EndsWith("/crm.contact.delete.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateLead_DealFails_DeleteReturnsFalse_KeepsOrphanContactIdInResponse()
    {
        var (_, client) = BuildClientWithHandler((req, _) =>
        {
            return req.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/crm.contact.add.json", StringComparison.Ordinal)
                    => Task.FromResult(StubHttpMessageHandler.Ok("""{"result":99}""")),
                var p when p.EndsWith("/crm.deal.add.json", StringComparison.Ordinal)
                    => Task.FromResult(StubHttpMessageHandler.Ok("""{"error":"DEAL_REJECTED","error_description":"reason"}""")),
                var p when p.EndsWith("/crm.contact.delete.json", StringComparison.Ordinal)
                    => Task.FromResult(StubHttpMessageHandler.Ok("""{"result":false}""")),
                _ => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))
            };
        });

        var result = await client.CreateLeadAsync(NewResponse(), NewSettings(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("99", result.ContactId);
        Assert.Contains("99", result.Error ?? string.Empty);
    }

    [Fact]
    public async Task CreateDealForContact_EmptyContactId_ReturnsFailureWithoutHttpCalls()
    {
        var (handler, client) = BuildClientWithHandler((_, _) =>
            Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}")));

        var result = await client.CreateDealForContactAsync(
            NewResponse(),
            string.Empty,
            NewSettings(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("ID", result.Error ?? string.Empty);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task CreateDealForContact_EmptyWebhook_ReturnsDemoSuccess()
    {
        var client = BuildClient((_, _) => throw new InvalidOperationException("Не должно быть HTTP-вызовов"));

        var result = await client.CreateDealForContactAsync(
            NewResponse(),
            "42",
            NewSettings(webhook: string.Empty),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("42", result.ContactId);
    }

    [Fact]
    public async Task GetExistingLeads_FollowsPagination_AndMapsContactFields()
    {
        var (handler, client) = BuildClientWithHandler((req, body) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/crm.deal.list.json", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body);
                var start = doc.RootElement.TryGetProperty("start", out var startProp) ? startProp.GetInt32() : 0;
                if (start == 0)
                {
                    return Task.FromResult(StubHttpMessageHandler.Ok(
                        """
                        {
                          "result": [
                            {"ID":"1","TITLE":"Отклик Авито: Курьер — Иванов И","COMMENTS":"ФИО: Иванов И\nТелефон: +79000000001","DATE_CREATE":"2026-05-01T10:00:00+03:00","CONTACT_ID":"100"}
                          ],
                          "next": 50
                        }
                        """));
                }

                return Task.FromResult(StubHttpMessageHandler.Ok(
                    """
                    {
                      "result": [
                        {"ID":"2","TITLE":"Отклик Авито: Кладовщик — Петров П","COMMENTS":"ФИО: Петров П\nГород: Москва","DATE_CREATE":"2026-05-02T10:00:00+03:00","CONTACT_ID":"200"}
                      ]
                    }
                    """));
            }

            if (path.EndsWith("/crm.contact.get.json", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body);
                var id = doc.RootElement.GetProperty("id").GetString();
                if (id == "100")
                {
                    return Task.FromResult(StubHttpMessageHandler.Ok(
                        """
                        {"result":{"NAME":"Иван","LAST_NAME":"Иванов","SECOND_NAME":"","PHONE":[{"VALUE":"+79000000001"}]}}
                        """));
                }

                return Task.FromResult(StubHttpMessageHandler.Ok(
                    """{"result":{"NAME":"Пётр","LAST_NAME":"Петров"}}"""));
            }

            return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"));
        });

        var responses = await client.GetExistingLeadsAsync(NewSettings(), CancellationToken.None);

        Assert.Equal(2, responses.Count);
        var first = responses.Single(x => x.BitrixEntityId == "1");
        Assert.Equal("Иванов Иван", first.FullName);
        Assert.Equal("+79000000001", first.PhoneRaw);

        var second = responses.Single(x => x.BitrixEntityId == "2");
        Assert.Equal("Петров Пётр", second.FullName);

        Assert.Equal(2, handler.Calls.Count(c => c.Path.EndsWith("/crm.deal.list.json", StringComparison.Ordinal)));
    }

    private static AppSettings NewSettings(string? webhook = Webhook) => new()
    {
        Bitrix = new BitrixSettings
        {
            WebhookUrl = webhook ?? string.Empty,
            CheckDuplicatesInBitrix = true,
            LeadSource = "Авито",
            ResponsibleId = 1
        }
    };

    private static CandidateResponse NewResponse() => new()
    {
        FullName = "Иванов Иван Иванович",
        FirstName = "Иван",
        LastName = "Иванов",
        MiddleName = "Иванович",
        PhoneRaw = "+7 900 000-00-00",
        City = "Москва",
        Vacancy = "Продавец",
        AccountName = "TestAcc",
        VacancyUrl = "https://www.avito.ru/item/1",
        CreatedAt = new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc)
    };

    private static IBitrixClient BuildClient(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> handler)
    {
        var (_, client) = BuildClientWithHandler(handler);
        return client;
    }

    private static (StubHttpMessageHandler handler, IBitrixClient client) BuildClientWithHandler(
        Func<HttpRequestMessage, string, Task<HttpResponseMessage>> handler)
    {
        var stub = new StubHttpMessageHandler(handler);
        var factory = new StubHttpClientFactory(stub);
        var client = new BitrixClient(factory, new CandidateParser());
        return (stub, client);
    }
}

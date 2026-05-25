using System.Net;
using System.Net.Http;
using System.Text.Json;
using LeadFlow.Models;
using LeadFlow.Services;
using LeadFlow.Services.Bitrix;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

/// <summary>Статический <see cref="BitrixClient.DealCreationTemporarilyDisabled"/> — общий для процесса; тесты идут последовательно.</summary>
[CollectionDefinition("BitrixClient", DisableParallelization = true)]
public sealed class BitrixClientTestCollection;

[Collection("BitrixClient")]
public sealed class BitrixClientTests : IDisposable
{
    private readonly bool _previousDealCreationDisabled;

    public BitrixClientTests()
    {
        _previousDealCreationDisabled = BitrixClient.DealCreationTemporarilyDisabled;
        BitrixClient.DealCreationTemporarilyDisabled = false;
    }

    public void Dispose()
    {
        BitrixClient.DealCreationTemporarilyDisabled = _previousDealCreationDisabled;
    }

    private const string Webhook = "https://b24-test.bitrix24.ru/rest/1/abc/";

    [Fact]
    public void RestJsonPreserveFieldNames_SerializesBitrixContactFieldsWithCorrectCasing()
    {
        var contactRequest = new
        {
            fields = new
            {
                NAME = "Иван",
                LAST_NAME = "Иванов",
                SECOND_NAME = "Иванович",
                PHONE = new[] { new { VALUE = "+79001234567", VALUE_TYPE = "WORK" } }
            }
        };

        var json = JsonSerializer.Serialize(contactRequest, BitrixClient.RestJsonPreserveFieldNames);

        using var doc = JsonDocument.Parse(json);
        var fields = doc.RootElement.GetProperty("fields");
        Assert.Equal("Иван", fields.GetProperty("NAME").GetString());
        Assert.Equal("Иванов", fields.GetProperty("LAST_NAME").GetString());
        Assert.Equal("Иванович", fields.GetProperty("SECOND_NAME").GetString());
        var phone = fields.GetProperty("PHONE")[0];
        Assert.Equal("+79001234567", phone.GetProperty("VALUE").GetString());
        Assert.Equal("WORK", phone.GetProperty("VALUE_TYPE").GetString());

        Assert.DoesNotContain("lasT_NAME", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HasDuplicate_EmptyWebhook_DemoMode_Skips()
    {
        var client = BuildClient((_, _) => throw new InvalidOperationException("Не должно быть HTTP-вызовов"));
        var settings = NewSettings(webhook: string.Empty, demoMode: true);

        var result = await client.HasDuplicateAsync("79000000000", settings, CancellationToken.None);

        Assert.True(result.IsSkipped);
    }

    [Fact]
    public async Task HasDuplicate_EmptyWebhook_NoDemo_CheckBitrix_ReturnsUnavailable()
    {
        var client = BuildClient((_, _) => throw new InvalidOperationException("Не должно быть HTTP-вызовов"));
        var settings = NewSettings(webhook: string.Empty, demoMode: false);

        var result = await client.HasDuplicateAsync("79000000000", settings, CancellationToken.None);

        Assert.True(result.IsUnavailable);
        Assert.Contains("вебхука", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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
    public async Task CreateLead_WhenDealCreationDisabled_SuppressesWithoutHttp()
    {
        var previous = BitrixClient.DealCreationTemporarilyDisabled;
        try
        {
            BitrixClient.DealCreationTemporarilyDisabled = true;
            var (handler, client) = BuildClientWithHandler((_, _) =>
                Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{}")));

            var result = await client.CreateLeadAsync(NewResponse(), NewSettings(), CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.True(result.BitrixCreationSuppressed);
            Assert.Empty(handler.Calls);
        }
        finally
        {
            BitrixClient.DealCreationTemporarilyDisabled = previous;
        }
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
    public async Task CreateLead_DealRequest_IncludesConfiguredUserFields()
    {
        string? dealBody = null;
        var (_, client) = BuildClientWithHandler((req, body) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/crm.deal.add.json", StringComparison.Ordinal))
            {
                dealBody = body;
            }

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
        response.Age = 28;
        response.City = "Пермь";
        response.Vacancy = "Курьер";

        var settings = NewSettings();
        settings.Bitrix.DealAgeUfCode = "UF_CRM_AGE";
        settings.Bitrix.DealProfessionUfCode = "UF_CRM_PROF";
        settings.Bitrix.DealCityUfCode = "UF_CRM_CITY";

        await client.CreateLeadAsync(response, settings, CancellationToken.None);

        Assert.NotNull(dealBody);
        using var doc = JsonDocument.Parse(dealBody!);
        var fields = doc.RootElement.GetProperty("fields");
        Assert.Equal("28", fields.GetProperty("UF_CRM_AGE").GetString());
        Assert.Equal("Курьер", fields.GetProperty("UF_CRM_PROF").GetString());
        Assert.Equal("Пермь", fields.GetProperty("UF_CRM_CITY").GetString());
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
    public async Task CreateDealForContact_EmptyWebhook_DemoMode_ReturnsDemoSuccess()
    {
        var client = BuildClient((_, _) => throw new InvalidOperationException("Не должно быть HTTP-вызовов"));

        var result = await client.CreateDealForContactAsync(
            NewResponse(),
            "42",
            NewSettings(webhook: string.Empty, demoMode: true),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("42", result.ContactId);
    }

    [Fact]
    public async Task CreateDealForContact_EmptyWebhook_NoDemo_ReturnsFailure()
    {
        var client = BuildClient((_, _) => throw new InvalidOperationException("Не должно быть HTTP-вызовов"));

        var result = await client.CreateDealForContactAsync(
            NewResponse(),
            "42",
            NewSettings(webhook: string.Empty, demoMode: false),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("вебхука", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateLead_EmptyWebhook_NoDemo_ReturnsFailure()
    {
        var client = BuildClient((_, _) => throw new InvalidOperationException("Не должно быть HTTP-вызовов"));

        var result = await client.CreateLeadAsync(NewResponse(), NewSettings(webhook: string.Empty, demoMode: false), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("вебхука", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateLead_ContactApiError_DoesNotCallDealAdd()
    {
        var (handler, client) = BuildClientWithHandler((req, _) =>
        {
            return req.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/crm.contact.add.json", StringComparison.Ordinal)
                    => Task.FromResult(StubHttpMessageHandler.Ok(
                        """{"error":"ACCESS_DENIED","error_description":"no"}""")),
                _ => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))
            };
        });

        var result = await client.CreateLeadAsync(NewResponse(), NewSettings(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("ACCESS_DENIED", result.Error ?? string.Empty);
        Assert.Single(handler.Calls);
        Assert.EndsWith("/crm.contact.add.json", handler.Calls[0].Path);
    }

    [Fact]
    public async Task CreateLead_Idempotency_DealListHit_SkipsContactAndDeal()
    {
        var response = NewResponse();
        response.AccountId = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");
        response.Source = "Avito";
        response.SourceResponseId = "src-99";

        var (handler, client) = BuildClientWithHandler((req, body) =>
        {
            return req.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/crm.deal.list.json", StringComparison.Ordinal)
                    => Task.FromResult(StubHttpMessageHandler.Ok(
                        """
                        {"result":[{"ID":"500","CONTACT_ID":"600"}]}
                        """)),
                _ => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))
            };
        });

        var settings = NewSettings();
        settings.Bitrix.DealIdempotencyUfCode = "UF_CRM_TEST_ID";

        var result = await client.CreateLeadAsync(response, settings, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("500", result.EntityId);
        Assert.Equal("600", result.ContactId);
        Assert.Single(handler.Calls);
        Assert.EndsWith("/crm.deal.list.json", handler.Calls[0].Path);
        using var doc = JsonDocument.Parse(handler.Calls[0].Body);
        Assert.True(doc.RootElement.TryGetProperty("filter", out var filter));
        Assert.True(filter.TryGetProperty("UF_CRM_TEST_ID", out var keyProp));
        Assert.Contains("src-99", keyProp.GetString() ?? string.Empty);
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
                            {"ID":"1","TITLE":"Иванов И","COMMENTS":"ФИО: Иванов И\nТелефон: +79000000001","DATE_CREATE":"2026-05-01T10:00:00+03:00","CONTACT_ID":"100"}
                          ],
                          "next": 50
                        }
                        """));
                }

                return Task.FromResult(StubHttpMessageHandler.Ok(
                    """
                    {
                      "result": [
                        {"ID":"2","TITLE":"Петров П","COMMENTS":"ФИО: Петров П\nГород: Москва","DATE_CREATE":"2026-05-02T10:00:00+03:00","CONTACT_ID":"200"}
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

    private static AppSettings NewSettings(string? webhook = Webhook, bool demoMode = true) => new()
    {
        DemoModeEnabled = demoMode,
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
        AccountId = Guid.Parse("b2b2b2b2-b2b2-b2b2-b2b2-b2b2b2b2b2b2"),
        Source = "Avito",
        SourceResponseId = "avito-test-1",
        FullName = "Иванов Иван Иванович",
        FirstName = "Иван",
        LastName = "Иванов",
        MiddleName = "Иванович",
        PhoneRaw = "+7 900 000-00-00",
        Age = 25,
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

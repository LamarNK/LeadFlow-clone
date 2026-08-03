using System.Net;
using System.Text;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixWebhookValidatorTests
{
    private const string Webhook = "https://demo.bitrix24.ru/rest/1/secret/";

    [Fact]
    public async Task ValidateAsync_InvalidUrl_ReturnsError()
    {
        var validator = CreateValidator(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await validator.ValidateAsync("not-a-url", CancellationToken.None);

        Assert.Equal(BitrixValidationStatuses.Error, result.Status);
        Assert.Contains(result.Steps, x => x.Id == "format" && x.Status == BitrixValidationStepStatuses.Error);
        Assert.Contains(result.Steps, x => x.Id == "format" && x.Hint is not null);
    }

    [Theory]
    [InlineData("https://localhost/rest/1/secret")]
    [InlineData("https://127.0.0.1/rest/1/secret")]
    [InlineData("https://internal.example/rest/1/secret")]
    public void NormalizeWebhookUrl_RejectsNonBitrixHost(string input)
    {
        Assert.Null(BitrixWebhookValidator.NormalizeWebhookUrl(input));
    }

    [Fact]
    public async Task ValidateAsync_CrmOnlyScope_ReturnsOk()
    {
        var validator = CreateValidator(CrmOnlyHandler);
        var result = await validator.ValidateAsync(Webhook, CancellationToken.None);

        Assert.Equal(BitrixValidationStatuses.Ok, result.Status);
        Assert.Equal(5, result.Steps.Count);
        Assert.DoesNotContain(result.Steps, x => x.Status == BitrixValidationStepStatuses.Error);
    }

    [Fact]
    public async Task ValidateAsync_MissingCrmScope_ReturnsWarning()
    {
        var validator = CreateValidator(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("scope", StringComparison.Ordinal))
            {
                return JsonResponse("""{"result":["user"]}""");
            }

            return JsonResponse("""{"result":{}}""");
        });

        var result = await validator.ValidateAsync(Webhook, CancellationToken.None);

        Assert.Equal(BitrixValidationStatuses.Warning, result.Status);
        Assert.Contains(result.Steps, x => x.Id == "scope" && x.Status == BitrixValidationStepStatuses.Warning);
        Assert.Contains(result.Steps, x => x.Id == "scope" && x.Hint is not null);
    }

    [Fact]
    public async Task ValidateAsync_InvalidCredentials_ReturnsFriendlyMessage()
    {
        var validator = CreateValidator(_ =>
            JsonResponse("""{"error":"INVALID_CREDENTIALS","error_description":"Invalid token"}""", HttpStatusCode.Unauthorized));

        var result = await validator.ValidateAsync(Webhook, CancellationToken.None);

        Assert.Equal(BitrixValidationStatuses.Error, result.Status);
        Assert.Contains(result.Steps, x => x.Id == "connectivity" && x.Message.Contains("не узнал", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Steps, x => x.Id == "connectivity" && x.Hint is not null);
    }

    [Fact]
    public async Task ValidateAsync_ConnectivityError_ReturnsError()
    {
        var validator = CreateValidator(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var result = await validator.ValidateAsync(Webhook, CancellationToken.None);

        Assert.Equal(BitrixValidationStatuses.Error, result.Status);
        Assert.Contains(result.Steps, x => x.Id == "connectivity" && x.Status == BitrixValidationStepStatuses.Error);
    }

    [Theory]
    [InlineData("https://demo.bitrix24.ru/rest/1/abc/", "https://demo.bitrix24.ru/rest/1/***/")]
    [InlineData("https://demo.bitrix24.ru/rest/1/abc", "https://demo.bitrix24.ru/rest/1/***/")]
    public void MaskWebhookUrl_MasksSecretSegment(string input, string expected)
    {
        Assert.Equal(expected, BitrixWebhookValidator.MaskWebhookUrl(input));
    }

    private static HttpResponseMessage CrmOnlyHandler(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.Contains("scope", StringComparison.Ordinal))
        {
            return JsonResponse("""{"result":["crm"]}""");
        }

        if (path.Contains("crm.contact.fields", StringComparison.Ordinal))
        {
            return JsonResponse("""{"result":{"ID":{}}}""");
        }

        if (path.Contains("crm.contact.list", StringComparison.Ordinal)
            || path.Contains("crm.deal.list", StringComparison.Ordinal))
        {
            return JsonResponse("""{"result":[]}""");
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static BitrixWebhookValidator CreateValidator(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var factory = new StubHttpClientFactory(new StubHttpMessageHandler(handler));
        return new BitrixWebhookValidator(factory);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpClientFactory(StubHttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}

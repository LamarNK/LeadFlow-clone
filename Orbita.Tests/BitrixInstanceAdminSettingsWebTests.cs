using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class BitrixInstanceAdminSettingsWebTests
{
    [Fact]
    public void MapEditor_PopulatesAndKeepsPerInstanceIntegrationSettings()
    {
        var settings = CreateIntegrationSettings(checkDuplicates: false);
        var detail = CreateInstance(Guid.NewGuid(), Guid.NewGuid(), settings);

        var editor = MySettingsService.MapEditor(detail);
        var validatedEditor = editor.WithLiveValidation(null, null);

        Assert.Equal(settings, editor.IntegrationSettings);
        Assert.Equal("Lead", editor.IntegrationSettings.EntityType);
        Assert.Equal(501, editor.IntegrationSettings.ResponsibleId);
        Assert.False(editor.IntegrationSettings.CheckDuplicatesInBitrix);
        Assert.Equal(settings, validatedEditor.IntegrationSettings);
    }

    [Fact]
    public async Task SaveBitrixInstanceAsync_Create_SendsIntegrationSettingsToApi()
    {
        var officeId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        CreateBitrixInstanceRequest? captured = null;
        using var handler = new RecordingHttpMessageHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/panel/bitrix-instances", request.RequestUri!.AbsolutePath);
            captured = await request.Content!.ReadFromJsonAsync<CreateBitrixInstanceRequest>(ct);
            return JsonResponse(CreateInstance(
                instanceId,
                officeId,
                captured!.IntegrationSettings!));
        });
        var (service, http) = CreateService(handler);
        using (http)
        {
            var result = await service.SaveBitrixInstanceAsync(
                CreateForm(id: null, webhookUrl: "https://portal.bitrix24.ru/rest/1/secret/"),
                officeId);

            Assert.True(result.Success, result.Error);
            Assert.Equal(instanceId, result.InstanceId);
        }

        Assert.NotNull(captured);
        Assert.NotNull(captured.IntegrationSettings);
        Assert.Equal("Lead", captured.IntegrationSettings.EntityType);
        Assert.Equal(501, captured.IntegrationSettings.ResponsibleId);
        Assert.Equal("Источник", captured.IntegrationSettings.LeadSource);
        Assert.Equal("UF_IDEMPOTENCY", captured.IntegrationSettings.DealIdempotencyUfCode);
        Assert.Equal("UF_AGE", captured.IntegrationSettings.DealAgeUfCode);
        Assert.Equal("UF_PROFESSION", captured.IntegrationSettings.DealProfessionUfCode);
        Assert.Equal("UF_CITY", captured.IntegrationSettings.DealCityUfCode);
        Assert.False(captured.IntegrationSettings.CheckDuplicatesInBitrix);
    }

    [Fact]
    public async Task SaveBitrixInstanceAsync_Edit_SendsIntegrationSettingsWithoutResettingHiddenValues()
    {
        var officeId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        UpdateBitrixInstanceRequest? captured = null;
        using var handler = new RecordingHttpMessageHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal($"/api/v1/panel/bitrix-instances/{instanceId:D}", request.RequestUri!.AbsolutePath);
            captured = await request.Content!.ReadFromJsonAsync<UpdateBitrixInstanceRequest>(ct);
            return JsonResponse(CreateInstance(
                instanceId,
                officeId,
                captured!.IntegrationSettings!));
        });
        var (service, http) = CreateService(handler);
        using (http)
        {
            var result = await service.SaveBitrixInstanceAsync(
                CreateForm(instanceId, webhookUrl: null),
                officeId);

            Assert.True(result.Success, result.Error);
            Assert.Equal(instanceId, result.InstanceId);
        }

        Assert.NotNull(captured);
        Assert.Null(captured.WebhookUrl);
        Assert.NotNull(captured.IntegrationSettings);
        Assert.Equal("Lead", captured.IntegrationSettings.EntityType);
        Assert.Equal(501, captured.IntegrationSettings.ResponsibleId);
        Assert.False(captured.IntegrationSettings.CheckDuplicatesInBitrix);
    }

    private static SaveBitrixInstanceFormModel CreateForm(Guid? id, string? webhookUrl) => new()
    {
        Id = id,
        Name = "Portal",
        Signature = "CRM-1",
        WebhookUrl = webhookUrl,
        IsEnabled = true,
        EntityType = " Lead ",
        ResponsibleId = 501,
        LeadSource = " Источник ",
        DealIdempotencyUfCode = " UF_IDEMPOTENCY ",
        DealAgeUfCode = " UF_AGE ",
        DealProfessionUfCode = " UF_PROFESSION ",
        DealCityUfCode = " UF_CITY ",
        CheckDuplicatesInBitrix = false
    };

    private static BitrixInstanceIntegrationSettingsDto CreateIntegrationSettings(
        bool checkDuplicates) =>
        new(
            "Lead",
            501,
            "Источник",
            "UF_IDEMPOTENCY",
            "UF_AGE",
            "UF_PROFESSION",
            "UF_CITY",
            checkDuplicates);

    private static BitrixInstanceDto CreateInstance(
        Guid id,
        Guid officeId,
        BitrixInstanceIntegrationSettingsDto settings)
    {
        var now = DateTime.UtcNow;
        return new BitrixInstanceDto(
            id,
            officeId,
            "Portal",
            "CRM-1",
            "https://portal.bitrix24.ru/rest/1/***/",
            "portal.bitrix24.ru",
            BitrixValidationStatuses.Ok,
            null,
            now,
            true,
            settings,
            null,
            0,
            null,
            now,
            now);
    }

    private static (SettingsService Service, HttpClient Http) CreateService(
        HttpMessageHandler handler)
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };
        var session = new AuthSession(accessor)
        {
            Token = "header.payload.signature"
        };
        var officeContext = new OfficeContext();
        var previewOptions = Options.Create(new DesignPreviewOptions());
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://orbita.test/")
        };
        var api = new OrbitaApiClient(http, session, officeContext, previewOptions);
        return (
            new SettingsService(api, accessor, officeContext, previewOptions),
            http);
    }

    private static HttpResponseMessage JsonResponse(BitrixInstanceDto instance) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(instance)
        };

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Auth;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class SettingsLogsSearchTests
{
    [Fact]
    public async Task LogsTab_WithWorkerAndSearch_ForwardsBothFilters()
    {
        var workerId = Guid.Parse("ef216d7e-62a8-4cc1-817d-3c65d4382f4b");
        Uri? logsRequestUri = null;
        using var handler = new RecordingHttpMessageHandler(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/v1/admin/offices":
                    return JsonResponse<IReadOnlyList<OfficeDto>>([]);
                case "/api/v1/admin/workers":
                    return JsonResponse<IReadOnlyList<AdminWorkerListItemDto>>([]);
                case "/api/v1/admin/logs":
                    logsRequestUri = request.RequestUri;
                    return JsonResponse(new ServiceLogsPageDto([], 0, 1, 50));
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        });
        var (service, http) = CreateService(handler);
        using (http)
        {
            await service.GetIndexAsync(
                "logs",
                "Captcha:",
                level: null,
                service: null,
                date: new DateTime(2026, 8, 21),
                workerId: workerId);
        }

        Assert.NotNull(logsRequestUri);
        Assert.Contains("q=Captcha%3A", logsRequestUri.Query, StringComparison.Ordinal);
        Assert.Contains($"workerId={workerId:D}", logsRequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("service=Orbita.Worker", logsRequestUri.Query, StringComparison.Ordinal);
    }

    private static (SettingsService Service, HttpClient Http) CreateService(HttpMessageHandler handler)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var session = new AuthSession(accessor) { Token = "header.payload.signature" };
        var officeContext = new OfficeContext();
        var previewOptions = Options.Create(new DesignPreviewOptions());
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://orbita.test/") };
        var api = new OrbitaApiClient(http, session, officeContext, previewOptions);
        return (new SettingsService(api, accessor, previewOptions), http);
    }

    private static HttpResponseMessage JsonResponse<T>(T payload) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}

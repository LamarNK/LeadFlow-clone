using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web;
using Orbita.Web.Options;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class CrmCallRecordingsWebTests : IClassFixture<WebApplicationFactory<WebApplicationEntryPoint>>
{
    private readonly WebApplicationFactory<WebApplicationEntryPoint> factory;
    private static readonly Guid CallId = Guid.Parse("4b999afe-f582-47dc-9140-7edecb0d03c1");
    private static readonly Guid CardId = Guid.Parse("f7d4fb8f-4917-4daf-9546-4e16e2288e81");
    private static readonly Guid OfficeId = Guid.Parse("e5cf55f1-a5a6-48c9-a370-e57433298e68");
    public CrmCallRecordingsWebTests(WebApplicationFactory<WebApplicationEntryPoint> factory) { this.factory = factory; }

    [Theory]
    [InlineData(PanelRoles.Admin)]
    [InlineData(PanelRoles.OfficeLead)]
    public async Task AuthorizedPageRendersCandidateOwnerAudioDownloadAndPreservedFilters(string role)
    {
        var handler = new ArchiveHandler();
        using var app = CreateApp(handler);
        using var client = CreateClient(app, role);
        var response = await client.GetAsync("/Crm/Recordings?from=2026-01-01&to=2026-01-01&tz=-300&phone=%2B79990001122&candidateName=%D0%98%D0%B2%D0%B0%D0%BD%20%D0%A2%D0%B5%D1%81%D1%82%D0%BE%D0%B2%D1%8B%D0%B9&managerUserId=a&direction=outgoing");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Записи звонков", html); Assert.Contains("Иван Тестовый", html);
        Assert.Contains("Сотрудник А", html); Assert.Contains("Офис тестовый", html);
        Assert.Contains($"/Crm/Card/{CardId}", html);
        Assert.Contains("preload=\"none\"", html); Assert.Contains("download=\"download\"", html);
        Assert.Contains("download=True", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=\"candidateName\"", html);
        Assert.Contains("value=\"Иван Тестовый\"", html);
        Assert.Contains("/css/orbita/crm-recordings.css", html);
        Assert.Contains("/js/crm-recordings.js", html);
        Assert.Contains("managerUserId=a", html); Assert.Contains("page=2", html); Assert.Contains("tz=-300", html);
        Assert.DoesNotContain("RecordingStoragePath", html);
        var uri = Assert.Single(handler.Requests, x => x.AbsolutePath == "/api/v1/crm/calls/recordings");
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(DateTimeOffset.Parse("2025-12-31T19:00:00Z"), DateTimeOffset.Parse(query["fromUtc"].ToString()));
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T19:00:00Z"), DateTimeOffset.Parse(query["toUtc"].ToString()));
        Assert.Equal("+79990001122", query["phone"]);
        Assert.Equal("Иван Тестовый", query["candidateName"]);
        if (role == PanelRoles.OfficeLead) Assert.Equal(OfficeId.ToString(), query["officeId"]);
        else Assert.False(query.ContainsKey("officeId"));
    }

    [Theory]
    [InlineData(PanelRoles.Manager)]
    [InlineData(PanelRoles.SeniorManager)]
    [InlineData(PanelRoles.Operator)]
    public async Task OtherRolesCannotOpenArchiveOrGuessDownloadUrl(string role)
    {
        var handler = new ArchiveHandler(); using var app = CreateApp(handler);
        using var client = CreateClient(app, role, withApiToken: false);
        foreach (var path in new[] { "/Crm/Recordings", $"/Crm/RecordingAudio?callId={CallId}&download=true" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/Account/AccessDenied", response.Headers.Location?.AbsolutePath);
        }
        Assert.DoesNotContain(handler.Requests, x => x.AbsolutePath.Contains("/calls/recordings"));
    }

    [Theory]
    [InlineData(PanelRoles.Admin)]
    [InlineData(PanelRoles.OfficeLead)]
    public async Task PlaybackIsInline_DownloadIsAttachment_AndSeekingReturnsPartialContent(string role)
    {
        var handler = new ArchiveHandler(); using var app = CreateApp(handler);
        using var client = CreateClient(app, role);
        var url = $"/Crm/RecordingAudio?callId={CallId}";
        var inline = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, inline.StatusCode);
        Assert.Equal("audio/wav", inline.Content.Headers.ContentType?.MediaType);
        Assert.Null(inline.Content.Headers.ContentDisposition);
        Assert.True(inline.Headers.CacheControl?.NoStore);
        Assert.Equal(ArchiveHandler.Audio, await inline.Content.ReadAsByteArrayAsync());
        var download = await client.GetAsync(url + "&download=true");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("call-test.wav", download.Content.Headers.ContentDisposition?.FileNameStar);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 3);
        var partial = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(ArchiveHandler.Audio[..4], await partial.Content.ReadAsByteArrayAsync());
        Assert.All(handler.Requests.Where(x => x.AbsolutePath.Contains("/calls/recordings/", StringComparison.Ordinal)),
            x => Assert.Equal($"/api/v1/crm/calls/recordings/{CallId}/content", x.AbsolutePath));
        var cardDownload = await client.GetAsync($"/Crm/CallRecording?callId={CallId}&download=true");
        Assert.Equal(HttpStatusCode.OK, cardDownload.StatusCode);
        Assert.Equal("attachment", cardDownload.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("call-test.wav", cardDownload.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(ArchiveHandler.Audio, await cardDownload.Content.ReadAsByteArrayAsync());
        Assert.Contains(handler.Requests, x => x.AbsolutePath == $"/api/v1/crm/calls/{CallId}/recording");
        var missing = await client.GetAsync($"/Crm/RecordingAudio?callId={Guid.NewGuid()}&download=true");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task EmptyArchiveAndApiFailureHaveDistinctMessages_AndFragmentsKeepControls()
    {
        var handler = new ArchiveHandler { Empty = true }; using var app = CreateApp(handler);
        using var client = CreateClient(app, PanelRoles.OfficeLead);
        var empty = WebUtility.HtmlDecode(await client.GetStringAsync("/Crm/Recordings"));
        Assert.Contains("Записей по этим условиям нет", empty);
        Assert.DoesNotContain("Не удалось загрузить записи", empty);
        handler.Fail = true;
        var failure = WebUtility.HtmlDecode(await client.GetStringAsync("/Crm/Recordings"));
        Assert.Contains("Не удалось загрузить записи", failure);
        Assert.DoesNotContain("Записей по этим условиям нет", failure);
        handler.Fail = false; handler.Empty = false;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Crm/Recordings");
        request.Headers.Add("X-Orbita-Content-Only", "1");
        var fragment = await client.SendAsync(request);
        var html = WebUtility.HtmlDecode(await fragment.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, fragment.StatusCode);
        Assert.DoesNotContain("<html", html); Assert.Contains("<audio", html);
        Assert.Contains("download=\"download\"", html); Assert.Contains("Иван Тестовый", html);
    }

    private WebApplicationFactory<WebApplicationEntryPoint> CreateApp(ArchiveHandler handler) =>
        factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development").ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.PostConfigure<DesignPreviewOptions>(options => options.Enabled = false);
            services.AddHttpClient<OrbitaApiClient>(client => client.BaseAddress = new Uri("https://archive.test/"))
                .ConfigurePrimaryHttpMessageHandler(() => handler);
        }));

    private static HttpClient CreateClient(WebApplicationFactory<WebApplicationEntryPoint> app, string role, bool withApiToken = true)
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim(ClaimTypes.NameIdentifier, "lead"), new Claim(ClaimTypes.Name, "Тестовый пользователь"),
            new Claim(ClaimTypes.Role, role), new Claim(OfficeClaims.OfficeId, OfficeId.ToString()),
            new Claim(PanelPermissions.ClaimType, PanelPermissions.CrmBoard)
        }, CookieAuthenticationDefaults.AuthenticationScheme));
        var ticket = options.TicketDataFormat.Protect(new AuthenticationTicket(principal, CookieAuthenticationDefaults.AuthenticationScheme));
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={ticket}" +
            (withApiToken ? $"; {AuthSession.TokenCookieName}=header.payload.signature" : ""));
        return client;
    }

    private sealed class ArchiveHandler : HttpMessageHandler
    {
        public static readonly byte[] Audio = [82, 73, 70, 70, 4, 0, 0, 0, 87, 65, 86, 69];
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public bool Empty { get; set; }
        public bool Fail { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/crm/calls/recordings")
            {
                if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                var rows = Empty ? Array.Empty<CrmCallRecordingRowDto>() : new[] { new CrmCallRecordingRowDto(CallId,
                    CardId, "Иван Тестовый", "79990001122", new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc),
                    75, CrmCallDirections.Outgoing, "Сотрудник А", "Офис тестовый") };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                    new CrmCallRecordingsDto(Empty ? 0 : 31, 1, 30, rows, [new("a", "Сотрудник А")])) });
            }
            if (path == $"/api/v1/crm/calls/recordings/{CallId}/content"
                || path == $"/api/v1/crm/calls/{CallId}/recording")
            {
                var content = new ByteArrayContent(Audio);
                content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = "call-test.wav" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

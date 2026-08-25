using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LeadFlow.Core.Services.Multilogin;
using Microsoft.Extensions.Http;

namespace Orbita.Tests;

public sealed class MultiloginApiClientTests
{
    private const string Token = "mlx-automation-token";
    private const string FolderId = "11111111-2222-3333-4444-555555555555";
    private const string ProfileId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    [Fact]
    public async Task StartProfileAsync_UsesConfirmedV2Path_AndParsesPort()
    {
        HttpRequestMessage? captured = null;
        var sut = CreateClient(request =>
        {
            captured = CloneRequest(request);
            return Json(HttpStatusCode.OK, """{"data":{"port":35001}}""");
        });

        var result = await sut.StartProfileAsync(ValidOptions(), FolderId, ProfileId);

        Assert.Equal(35001, result.Port);
        Assert.Equal("http://127.0.0.1:35001", result.BrowserUrl);
        Assert.Null(result.WebSocketDebuggerUrl);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal(
            $"https://launcher.mlx.yt:45001/api/v2/profile/f/{FolderId}/p/{ProfileId}/start?automation_type=puppeteer&headless_mode=false",
            captured.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal(Token, captured.Headers.Authorization?.Parameter);
        Assert.Contains("application/json", captured.Headers.Accept.Select(static x => x.MediaType));
        Assert.DoesNotContain("api.multilogin.com", captured.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartProfileAsync_ParsesStringPort()
    {
        var sut = CreateClient(_ => Json(HttpStatusCode.OK, """{"data":{"port":"41234"}}"""));

        var result = await sut.StartProfileAsync(ValidOptions(), FolderId, ProfileId);

        Assert.Equal(41234, result.Port);
        Assert.Equal("http://127.0.0.1:41234", result.BrowserUrl);
    }

    [Fact]
    public async Task StartProfileAsync_StripsVersionedLauncherBase()
    {
        HttpRequestMessage? captured = null;
        var sut = CreateClient(request =>
        {
            captured = CloneRequest(request);
            return Json(HttpStatusCode.OK, """{"data":{"port":1}}""");
        });
        var options = new MultiloginConnectionOptions
        {
            LauncherUrl = "https://launcher.mlx.yt:45001/api/v2/",
            CloudApiUrl = "https://api.multilogin.com/",
            AutomationToken = Token
        };

        await sut.StartProfileAsync(options, FolderId, ProfileId);

        Assert.StartsWith(
            "https://launcher.mlx.yt:45001/api/v2/profile/",
            captured!.RequestUri?.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.DoesNotContain("/api/v2/api/v2/", captured.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartProfileAsync_EscapesIds()
    {
        HttpRequestMessage? captured = null;
        var sut = CreateClient(request =>
        {
            captured = CloneRequest(request);
            return Json(HttpStatusCode.OK, """{"data":{"port":2}}""");
        });

        await sut.StartProfileAsync(ValidOptions(), "folder/a", "profile b");

        Assert.Equal(
            "https://launcher.mlx.yt:45001/api/v2/profile/f/folder%2Fa/p/profile%20b/start?automation_type=puppeteer&headless_mode=false",
            captured!.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task StartProfileAsync_HttpError_DoesNotLeakToken()
    {
        var sut = CreateClient(_ => Json(HttpStatusCode.Unauthorized, """{"message":"nope"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartProfileAsync(ValidOptions(), FolderId, ProfileId));

        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartProfileAsync_MissingPort_Throws()
    {
        var sut = CreateClient(_ => Json(HttpStatusCode.OK, """{"data":{}}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartProfileAsync(ValidOptions(), FolderId, ProfileId));
        Assert.Contains("data.port", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartProfileAsync_InvalidJson_Throws()
    {
        var sut = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json")
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartProfileAsync(ValidOptions(), FolderId, ProfileId));
    }

    [Fact]
    public async Task StartProfileAsync_WithoutLauncher_ThrowsBeforeHttp()
    {
        var calls = 0;
        var sut = CreateClient(_ =>
        {
            calls++;
            return Json(HttpStatusCode.OK, """{"data":{"port":1}}""");
        });

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.StartProfileAsync(
                new MultiloginConnectionOptions { AutomationToken = Token },
                FolderId,
                ProfileId));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task StartProfileAsync_WithoutToken_ThrowsBeforeHttp()
    {
        var calls = 0;
        var sut = CreateClient(_ =>
        {
            calls++;
            return Json(HttpStatusCode.OK, """{"data":{"port":1}}""");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartProfileAsync(
                new MultiloginConnectionOptions { LauncherUrl = "https://launcher.mlx.yt:45001" },
                FolderId,
                ProfileId));
        Assert.Contains("token", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task StopProfileAsync_UsesConfirmedV1Path_WithoutFolder()
    {
        HttpRequestMessage? captured = null;
        var sut = CreateClient(request =>
        {
            captured = CloneRequest(request);
            return Json(HttpStatusCode.OK, """{"status":"ok"}""");
        });

        await sut.StopProfileAsync(ValidOptions(), "  " + ProfileId + "  ");

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal(
            $"https://launcher.mlx.yt:45001/api/v1/profile/stop/p/{ProfileId}",
            captured.RequestUri?.AbsoluteUri);
        Assert.Equal(Token, captured.Headers.Authorization?.Parameter);
        Assert.DoesNotContain("/f/", captured.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("api.multilogin.com", captured.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopProfileAsync_HttpError_DoesNotLeakToken()
    {
        var sut = CreateClient(_ => Json(HttpStatusCode.BadGateway, """{"error":"down"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StopProfileAsync(ValidOptions(), ProfileId));
        Assert.Contains("502", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartProfileAsync_HonorsCancellation()
    {
        var sut = CreateClient((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Json(HttpStatusCode.OK, """{"data":{"port":1}}""");
        });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.StartProfileAsync(ValidOptions(), FolderId, ProfileId, cts.Token));
    }

    [Fact]
    public void Client_DoesNotExposeSearch()
    {
        var names = typeof(MultiloginApiClient).GetMethods()
            .Select(static m => m.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(IMultiloginApiClient.StartProfileAsync), names);
        Assert.Contains(nameof(IMultiloginApiClient.StopProfileAsync), names);
        Assert.DoesNotContain("SearchProfilesAsync", names);
        Assert.DoesNotContain("ListProfilesAsync", names);
        Assert.DoesNotContain("ListFoldersAsync", names);
    }

    private static MultiloginConnectionOptions ValidOptions() => new()
    {
        LauncherUrl = "https://launcher.mlx.yt:45001/",
        CloudApiUrl = "https://api.multilogin.com",
        AutomationToken = "  " + Token + "  "
    };

    private static MultiloginApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        CreateClient((request, _) => handler(request));

    private static MultiloginApiClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) =>
        new(new StubFactory(new StubHandler(handler)));

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (source.Headers.Authorization is AuthenticationHeaderValue authorization)
        {
            clone.Headers.Authorization = authorization;
        }

        return clone;
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request, cancellationToken));
        }
    }
}

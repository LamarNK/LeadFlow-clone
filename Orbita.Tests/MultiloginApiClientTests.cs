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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HttpError_ExactTokenInBody_IsRemovedForStartAndStop(bool start)
    {
        var sut = CreateClient(_ => Json(
            HttpStatusCode.InternalServerError,
            $"{{\"error\":\"replay rejected\",\"hint\":\"{Token}\"}}"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeStartOrStop(sut, start));

        AssertSafeHttpError(ex.Message, start, HttpStatusCode.InternalServerError);
        Assert.Contains("replay rejected", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HttpError_BearerTokenInBody_IsRemovedForStartAndStop(bool start)
    {
        var sut = CreateClient(_ => Json(
            HttpStatusCode.Forbidden,
            $"{{\"auth\":\"Bearer {Token}\",\"alt\":\"bearer {Token}\",\"upper\":\"BEARER {Token}\",\"ok\":\"safe-error\"}}"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeStartOrStop(sut, start));

        AssertSafeHttpError(ex.Message, start, HttpStatusCode.Forbidden);
        Assert.Contains("safe-error", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer " + Token, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bearer " + Token, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpError_SafeBody_IsPreserved()
    {
        var sut = CreateClient(_ => Json(HttpStatusCode.Unauthorized, """{"message":"profile is already running"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartProfileAsync(ValidOptions(), FolderId, ProfileId));

        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.Contains("profile is already running", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpError_SanitizesTokenBeforeTruncation()
    {
        var keepMarker = "KEEP_ME";
        var droppedMarker = "DROP_ME";
        var body = Token + new string('X', 480) + keepMarker + new string('Y', 80) + droppedMarker;
        var sut = CreateClient(_ => Json(HttpStatusCode.BadGateway, body));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartProfileAsync(ValidOptions(), FolderId, ProfileId));

        Assert.Contains(keepMarker, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(droppedMarker, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.Contains("…", ex.Message, StringComparison.Ordinal);
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
    public void Client_ExposesSearchProfiles_NotFolders()
    {
        var names = typeof(MultiloginApiClient).GetMethods()
            .Select(static m => m.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(IMultiloginApiClient.StartProfileAsync), names);
        Assert.Contains(nameof(IMultiloginApiClient.StopProfileAsync), names);
        Assert.Contains(nameof(IMultiloginApiClient.SearchProfilesAsync), names);
        Assert.DoesNotContain("ListProfilesAsync", names);
        Assert.DoesNotContain("ListFoldersAsync", names);
    }

    [Fact]
    public async Task SearchProfilesAsync_PostsConfirmedCloudPath_AndMapsIdFolderName()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var sut = CreateClient(request =>
        {
            captured = CloneRequest(request);
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(
                HttpStatusCode.OK,
                """
                {"status":{"http_code":200},"data":{"profiles":[
                  {"id":"profile-a","folder_id":"folder-a","name":"Авито 50","proxy":{"username":"u","password":"secret"},"username":"leak","password":"leak"},
                  {"id":"profile-b","folder_id":"folder-b","name":"Second"}
                ],"total_count":2}}
                """);
        });

        var result = await sut.SearchProfilesAsync(ValidOptions());

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal(new MultiloginProfileSummary("profile-a", "folder-a", "Авито 50"), result.Profiles[0]);
        Assert.Equal(new MultiloginProfileSummary("profile-b", "folder-b", "Second"), result.Profiles[1]);
        Assert.All(result.Profiles, static p =>
        {
            Assert.Null(p.GetType().GetProperty("Proxy"));
            Assert.Null(p.GetType().GetProperty("Username"));
            Assert.Null(p.GetType().GetProperty("Password"));
            Assert.Null(p.GetType().GetProperty("Token"));
        });
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal("https://api.multilogin.com/profile/search", captured.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal(Token, captured.Headers.Authorization?.Parameter);
        Assert.Equal("application/json", captured.Content?.Headers.ContentType?.MediaType);
        using var doc = System.Text.Json.JsonDocument.Parse(body!);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("is_removed").GetBoolean());
        Assert.Equal(100, root.GetProperty("limit").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());
        Assert.Equal("", root.GetProperty("search_text").GetString());
        Assert.Equal("all", root.GetProperty("storage_type").GetString());
        Assert.Equal("created_at", root.GetProperty("order_by").GetString());
        Assert.Equal("asc", root.GetProperty("sort").GetString());
        Assert.Equal(7, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task SearchProfilesAsync_SkipsIncompleteProfiles()
    {
        var sut = CreateClient(_ => Json(
            HttpStatusCode.OK,
            """
            {"status":{"http_code":200},"data":{"profiles":[
              {"id":"ok","folder_id":"folder-ok","name":"Keep"},
              {"folder_id":"folder-missing-id","name":"NoId"},
              {"id":"missing-folder","name":"NoFolder"},
              {"id":"","folder_id":"folder-empty","name":"EmptyId"},
              {"id":"only-ws","folder_id":"  ","name":"WsFolder"}
            ],"total_count":5}}
            """));

        var result = await sut.SearchProfilesAsync(ValidOptions());

        Assert.False(result.IsComplete);
        var profile = Assert.Single(result.Profiles);
        Assert.Equal("ok", profile.ProfileId);
        Assert.Equal("folder-ok", profile.FolderId);
        Assert.Equal("Keep", profile.Name);
    }

    [Fact]
    public async Task SearchProfilesAsync_PaginatesUntilTotalCount()
    {
        var offsets = new List<int>();
        var sut = CreateClient(request =>
        {
            var json = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "{}";
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var offset = doc.RootElement.GetProperty("offset").GetInt32();
            Assert.Equal(100, doc.RootElement.GetProperty("limit").GetInt32());
            offsets.Add(offset);
            return Json(HttpStatusCode.OK, SearchPage(offset, total: 101));
        });

        var result = await sut.SearchProfilesAsync(ValidOptions());

        Assert.True(result.IsComplete);
        Assert.Equal([0, 100], offsets);
        Assert.Equal(101, result.Profiles.Count);
        Assert.Equal("id-0", result.Profiles[0].ProfileId);
        Assert.Equal("folder-100", result.Profiles[100].FolderId);
        Assert.Equal("P100", result.Profiles[100].Name);
    }

    [Fact]
    public async Task SearchProfilesAsync_Unauthorized_DoesNotLeakTokenOrSecrets()
    {
        var sut = CreateClient(_ => Json(
            HttpStatusCode.Unauthorized,
            """{"status":{"http_code":401},"password":"super-secret","token":"mlx-automation-token","proxy":"1.2.3.4"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SearchProfilesAsync(ValidOptions()));

        Assert.Contains("profile/search", ex.Message, StringComparison.Ordinal);
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1.2.3.4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchProfilesAsync_EmptyProfiles_ReturnsEmpty()
    {
        var sut = CreateClient(_ => Json(
            HttpStatusCode.OK,
            """{"status":{"http_code":200},"data":{"profiles":[],"total_count":0}}"""));

        var result = await sut.SearchProfilesAsync(ValidOptions());

        Assert.True(result.IsComplete);
        Assert.Empty(result.Profiles);
    }

    [Theory]
    [InlineData("""{"status":{"http_code":200},"password":"super-secret"}""", "нет data")]
    [InlineData("""{"status":{"http_code":200},"data":{"total_count":0},"token":"mlx-automation-token"}""", "нет profiles")]
    [InlineData("""{"status":{"http_code":200},"data":{"profiles":[],"proxy":"1.2.3.4"}}""", "нет total_count")]
    [InlineData("""not-json""", "некорректный JSON")]
    public async Task SearchProfilesAsync_MalformedResponse_ThrowsWithoutSecrets(string body, string reason)
    {
        var sut = CreateClient(_ => Json(HttpStatusCode.OK, body));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SearchProfilesAsync(ValidOptions()));

        Assert.Contains("profile/search", ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1.2.3.4", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(body, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchProfilesAsync_AllIncompleteProfiles_ThrowsAndIsNotEmptyComplete()
    {
        var sut = CreateClient(_ => Json(
            HttpStatusCode.OK,
            """
            {"status":{"http_code":200},"data":{"profiles":[
              {"folder_id":"folder-1","name":"NoId"},
              {"id":"p2","name":"NoFolder"}
            ],"total_count":2}}
            """));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SearchProfilesAsync(ValidOptions()));

        Assert.Contains("id или folder_id", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchProfilesAsync_EmptyProfilesWithoutZeroTotal_Throws()
    {
        var sut = CreateClient(_ => Json(
            HttpStatusCode.OK,
            """{"status":{"http_code":200},"data":{"profiles":[],"total_count":3}}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SearchProfilesAsync(ValidOptions()));

        Assert.Contains("неполный", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchProfilesAsync_MaxPages_ThrowsIncomplete_DoesNotReturnPartialCatalog()
    {
        var pages = 0;
        var total = MultiloginApiClient.ProfileSearchMaxPages * MultiloginApiClient.ProfileSearchPageSize + 1;
        var sut = CreateClient(_ =>
        {
            var offset = pages * MultiloginApiClient.ProfileSearchPageSize;
            pages++;
            return Json(HttpStatusCode.OK, SearchPage(offset, total));
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.SearchProfilesAsync(ValidOptions()));

        Assert.Equal(MultiloginApiClient.ProfileSearchMaxPages, pages);
        Assert.Contains("каталог неполный", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchProfilesAsync_UsesDefaultCloudUrl_WhenMissing()
    {
        HttpRequestMessage? captured = null;
        var sut = CreateClient(request =>
        {
            captured = CloneRequest(request);
            return Json(HttpStatusCode.OK, """{"status":{"http_code":200},"data":{"profiles":[],"total_count":0}}""");
        });

        await sut.SearchProfilesAsync(new MultiloginConnectionOptions
        {
            AutomationToken = Token,
            CloudApiUrl = "  "
        });

        Assert.Equal("https://api.multilogin.com/profile/search", captured!.RequestUri?.AbsoluteUri);
    }

    private static string SearchPage(int offset, int total)
    {
        var take = Math.Min(MultiloginApiClient.ProfileSearchPageSize, Math.Max(0, total - offset));
        var items = string.Join(
            ",",
            Enumerable.Range(offset, take)
                .Select(static i =>
                    "{\"id\":\"id-" + i + "\",\"folder_id\":\"folder-" + i + "\",\"name\":\"P" + i + "\"}"));
        return "{\"status\":{\"http_code\":200},\"data\":{\"profiles\":[" + items + "],\"total_count\":" + total + "}}";
    }

    private static Task InvokeStartOrStop(MultiloginApiClient sut, bool start) =>
        start
            ? sut.StartProfileAsync(ValidOptions(), FolderId, ProfileId)
            : sut.StopProfileAsync(ValidOptions(), ProfileId);

    private static void AssertSafeHttpError(string message, bool start, HttpStatusCode status)
    {
        Assert.Contains(start ? "profile/start" : "profile/stop", message, StringComparison.Ordinal);
        Assert.Contains(((int)status).ToString(), message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, message, StringComparison.Ordinal);
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

        if (source.Content is not null)
        {
            var text = source.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(
                text,
                Encoding.UTF8,
                source.Content.Headers.ContentType?.MediaType ?? "application/json");
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

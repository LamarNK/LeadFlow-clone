using System.Net;
using System.Net.Http;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Worker;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AdsPowerStartupDiagnosticsTests
{
    private const string SecretWs =
        "ws://127.0.0.1:9222/devtools/browser/abc-secret-guid?token=ws-token-SECRET";
    private const string SecretProxy = "http://proxy-user:proxy-pass@10.1.2.3:8000";
    private const string SecretCookie = "sessionid=cookie-value-SECRET";
    private const string SecretToken = "Bearer ads-token-SECRET";
    private const string SecretHtml = "<html><body>full page SECRET-HTML</body></html>";

    [Fact]
    public void SummarizeBrowserStart_Success_KeepsAllowlistedFields_AndDropsSecrets()
    {
        var json = $$"""
            {
              "code": 0,
              "msg": "success contact a.pakin797@gmail.com {{SecretToken}}",
              "data": {
                "ws": {
                  "puppeteer": "{{SecretWs}}",
                  "selenium": "127.0.0.1:9222"
                },
                "debug_port": "9222",
                "proxy": "{{SecretProxy}}",
                "cookie": "{{SecretCookie}}"
              }
            }
            """;

        var snapshot = AdsPowerStartupLogSanitizer.SummarizeBrowserStart(
            json,
            httpStatusCode: 200,
            transportException: null,
            duration: TimeSpan.FromMilliseconds(842),
            parsedWebSocketUrl: SecretWs,
            debugPort: "9222",
            openUrl: "https://www.avito.ru/profile/pro/items");

        Assert.True(snapshot.Ok);
        Assert.True(snapshot.HasWsPuppeteer);
        Assert.Equal(0, snapshot.AdsPowerCode);
        Assert.Equal(200, snapshot.HttpStatusCode);
        Assert.Equal("object", snapshot.DataKind);
        Assert.Equal("json", snapshot.PayloadKind);
        Assert.True(snapshot.DebugPortPresent);
        Assert.Equal("avito", snapshot.OpenUrlClass);
        Assert.Equal(842, snapshot.DurationMs, precision: 3);
        Assert.Contains("ws", snapshot.DataKeys!, StringComparison.Ordinal);
        Assert.Contains("proxy", snapshot.DataKeys!, StringComparison.Ordinal);
        Assert.Equal("ws", snapshot.Ws!.Scheme);
        Assert.Equal("loopback", snapshot.Ws.HostClass);
        Assert.Equal(9222, snapshot.Ws.Port);
        Assert.Equal("devtools-browser", snapshot.Ws.PathClass);
        Assert.True(snapshot.Ws.HasQuery);
        Assert.Equal(1, snapshot.Ws.QueryKeyCount);
        Assert.False(snapshot.Ws.HasUserInfo);
        Assert.DoesNotContain("@gmail.com", snapshot.AdsPowerMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretToken, snapshot.AdsPowerMessage, StringComparison.Ordinal);

        var blob = AdsPowerStartupLogSanitizer.SerializeForInspection(
            AdsPowerStartupLogSanitizer.ToProperties(snapshot));
        AssertNoSecrets(blob);
        Assert.DoesNotContain("10.1.2.3", blob, StringComparison.Ordinal);
        Assert.Contains("\"localApi.hasWsPuppeteer\":true", blob, StringComparison.Ordinal);
        Assert.Contains("devtools-browser", blob, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarizeBrowserStart_HtmlPayload_DoesNotCopyBody()
    {
        var snapshot = AdsPowerStartupLogSanitizer.SummarizeBrowserStart(
            SecretHtml,
            httpStatusCode: 502,
            transportException: null,
            duration: TimeSpan.FromSeconds(1),
            parsedWebSocketUrl: null,
            debugPort: null,
            openUrl: null);

        Assert.False(snapshot.Ok);
        Assert.Equal("html", snapshot.PayloadKind);
        Assert.Equal(SecretHtml.Length, snapshot.PayloadLength);
        Assert.False(snapshot.HasWsPuppeteer);
        Assert.Equal("missing", snapshot.DataKind);

        var blob = AdsPowerStartupLogSanitizer.SerializeForInspection(
            AdsPowerStartupLogSanitizer.ToProperties(snapshot));
        AssertNoSecrets(blob);
        Assert.DoesNotContain("<html", blob, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SummarizeBrowserStart_TransportException_HasTypeAndNoHttpStatusSuccess()
    {
        var transport = new HttpRequestException("Connection refused to " + SecretProxy);
        var snapshot = AdsPowerStartupLogSanitizer.SummarizeBrowserStart(
            payload: null,
            httpStatusCode: null,
            transportException: transport,
            duration: TimeSpan.FromSeconds(12),
            parsedWebSocketUrl: null,
            debugPort: null,
            openUrl: null);

        Assert.False(snapshot.Ok);
        Assert.Equal(typeof(HttpRequestException).FullName, snapshot.TransportExceptionType);
        Assert.Null(snapshot.HttpStatusCode);
        Assert.False(snapshot.HasWsPuppeteer);
        Assert.Equal("empty", snapshot.PayloadKind);

        var blob = AdsPowerStartupLogSanitizer.SerializeForInspection(
            AdsPowerStartupLogSanitizer.ToProperties(snapshot));
        AssertNoSecrets(blob);
        Assert.DoesNotContain("Connection refused", blob, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeWsEndpoint_MasksUserInfo_AndClassifiesLoopback()
    {
        var meta = AdsPowerStartupLogSanitizer.DescribeWsEndpoint(
            "ws://chrome:cdp-pass@" + "127.0.0.1:9222/devtools/browser/abc");

        Assert.NotNull(meta);
        Assert.True(meta!.HasUserInfo);
        Assert.Equal("loopback", meta.HostClass);
        Assert.Equal("devtools-browser", meta.PathClass);
        Assert.False(meta.HasQuery);

        var props = AdsPowerStartupLogSanitizer.ToProperties(
            AdsPowerStartupLogSanitizer.SummarizeBrowserStart(
                """{"code":0,"data":{"ws":{"puppeteer":"x"}}}""",
                200,
                null,
                TimeSpan.Zero,
                "ws://chrome:cdp-pass@127.0.0.1:9222/devtools/browser/abc",
                "9222",
                null));
        var blob = AdsPowerStartupLogSanitizer.SerializeForInspection(props);
        Assert.DoesNotContain("cdp-pass", blob, StringComparison.Ordinal);
        Assert.DoesNotContain("chrome:cdp-pass", blob, StringComparison.Ordinal);
    }

    [Fact]
    public void LimitText_MasksWsProxyEmailAndTruncates()
    {
        var raw = $"AdsPower {SecretWs} via {SecretProxy} mail a.pakin797@gmail.com " + new string('x', 400);
        var limited = AdsPowerStartupLogSanitizer.LimitText(raw);

        Assert.True(limited.Length <= AdsPowerStartupLogSanitizer.MaxMessageLength + 1);
        Assert.DoesNotContain(SecretWs, limited, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy-pass", limited, StringComparison.Ordinal);
        Assert.DoesNotContain("@gmail.com", limited, StringComparison.Ordinal);
        Assert.Contains("ws://***", limited, StringComparison.Ordinal);
    }

    [Fact]
    public void Trace_CdpHang_KeepsSameCorrelation_AndLastSuccessfulOps()
    {
        var events = new List<AdsPowerStartupDiagnosticEvent>();
        using var trace = AdsPowerStartupTrace.Begin("k1user", attempt: 2, events.Add);
        using (trace.Activate())
        {
            Assert.Same(trace, AdsPowerStartupTrace.Current);
            trace.SetStage("ожидание очереди AdsPower browser/start");
            var local = AdsPowerStartupLogSanitizer.SummarizeBrowserStart(
                $$"""{"code":0,"msg":"ok","data":{"ws":{"puppeteer":"{{SecretWs}}"},"debug_port":"9222"}}""",
                200,
                null,
                TimeSpan.FromMilliseconds(410),
                SecretWs,
                "9222",
                "https://www.avito.ru/profile/pro/items");
            Assert.NotNull(trace.RecordLocalApi(local));

            trace.SetStage("подключение CDP");
            Assert.NotNull(trace.RecordCdp("Connect", "подключение CDP", TimeSpan.FromMilliseconds(90), ok: true));

            trace.SetStage("поиск рабочей вкладки");
            Assert.NotNull(trace.RecordCdp(
                "PagesAsync",
                AdsPowerAvitoAutomationService.PageAcquireOperation,
                TimeSpan.FromMilliseconds(12),
                ok: true,
                pagesCount: 0,
                urlClasses: string.Empty));
            Assert.NotNull(trace.RecordCdp(
                "PagesAsync",
                AdsPowerAvitoAutomationService.PageAcquireRetryOperation,
                TimeSpan.FromMilliseconds(8),
                ok: true,
                pagesCount: 0,
                urlClasses: string.Empty));

            var timeout = AdsPowerAvitoAutomationService.CreateEmptyPagesAcquisitionTimeout();
            var failure = trace.RecordFailure(timeout);
            Assert.NotNull(failure);
            Assert.Null(trace.RecordFailure(new InvalidOperationException("duplicate")));
            Assert.Single(events, static e => e.Name == "startup_failure");
        }

        Assert.Null(AdsPowerStartupTrace.Current);
        Assert.Equal(
            ["browser_start", "cdp_connect", "pages_async", "pages_async", "startup_failure"],
            events.Select(e => e.Name).ToArray());
        Assert.All(events, e => Assert.Equal(trace.CorrelationId, e.Properties["startup.correlationId"]));
        Assert.All(events, e => Assert.Equal("k1user", e.Properties["adsPower.userId"]));
        Assert.All(events, e => Assert.Equal(2, e.Properties["startup.attempt"]));

        var failureEvent = events[^1];
        Assert.Equal("поиск рабочей вкладки", failureEvent.Properties["startup.stage"]);
        Assert.Equal("browser/start", failureEvent.Properties["startup.lastSuccessfulLocalApi"]);
        Assert.Equal("PagesAsync", failureEvent.Properties["startup.lastSuccessfulCdp"]);
        Assert.Equal(typeof(TimeoutException).FullName, failureEvent.Properties["error.type"]);
        Assert.Contains("список вкладок пуст", (string)failureEvent.Properties["error.message"]!, StringComparison.Ordinal);

        foreach (var evt in events)
        {
            AssertNoSecrets(AdsPowerStartupLogSanitizer.SerializeForInspection(evt.Properties));
        }

        Assert.True(AdsPowerCdpGuard.IsCdpTimeout(
            AdsPowerAvitoAutomationService.CreateEmptyPagesAcquisitionTimeout()));
        var night = new DateTime(2026, 8, 24, 20, 30, 0, DateTimeKind.Utc);
        var retry = WorkerAccountPassDelay.Resolve(
            retryAfter: TimeSpan.FromMinutes(1),
            polled: true,
            newResponses: 0,
            quietStreak: 4,
            backlog: false,
            historicalHeat: 0,
            utcNow: night);
        Assert.Equal(TimeSpan.FromMinutes(1), retry);
    }

    [Fact]
    public void Trace_LocalApiFailure_HasNoCdpSuccess_AndIsDistinguishableFromCdpHang()
    {
        var events = new List<AdsPowerStartupDiagnosticEvent>();
        using var trace = AdsPowerStartupTrace.Begin("k1user", attempt: 1, events.Add);
        trace.Activate();
        trace.SetStage("ожидание очереди AdsPower browser/start");

        var snapshot = AdsPowerStartupLogSanitizer.SummarizeBrowserStart(
            payload: null,
            httpStatusCode: null,
            transportException: new HttpRequestException("timed out"),
            duration: TimeSpan.FromSeconds(45),
            parsedWebSocketUrl: null,
            debugPort: null,
            openUrl: null);
        trace.RecordLocalApi(snapshot);
        var failure = trace.RecordFailure(new HttpRequestException("timed out"));

        Assert.Equal(["browser_start", "startup_failure"], events.Select(e => e.Name).ToArray());
        Assert.False((bool)events[0].Properties["localApi.hasWsPuppeteer"]!);
        Assert.Equal(typeof(HttpRequestException).FullName, events[0].Properties["localApi.transportExceptionType"]);
        Assert.Null(events[0].Properties["localApi.httpStatus"]);
        Assert.Null(failure!.Properties["startup.lastSuccessfulLocalApi"]);
        Assert.Null(failure.Properties["startup.lastSuccessfulCdp"]);
        Assert.Equal("ожидание очереди AdsPower browser/start", failure.Properties["startup.stage"]);
        Assert.Equal("local_api", events[0].Properties["startup.boundary"]);
        Assert.Null(events[0].Properties["cdp.call"]);

        Assert.False(AdsPowerCdpGuard.IsCdpTimeout(new HttpRequestException("timed out")));
        Assert.True(IsLocalApiFailure(events));
        Assert.False(IsCdpHang(events));
    }

    [Fact]
    public void Trace_CdpHangAfterWs_IsDistinguishableFromLocalApiFailure()
    {
        var events = new List<AdsPowerStartupDiagnosticEvent>();
        using var trace = AdsPowerStartupTrace.Begin("k1user", 1, events.Add);
        trace.Activate();
        trace.SetStage("ожидание очереди AdsPower browser/start");
        trace.RecordLocalApi(AdsPowerStartupLogSanitizer.SummarizeBrowserStart(
            """{"code":0,"data":{"ws":{"puppeteer":"ws://127.0.0.1:9222/devtools/browser/abc"}}}""",
            200,
            null,
            TimeSpan.FromMilliseconds(200),
            "ws://127.0.0.1:9222/devtools/browser/abc",
            "9222",
            null));
        trace.SetStage("подключение CDP");
        trace.RecordCdp("Connect", "подключение CDP", TimeSpan.FromSeconds(15), ok: false);
        trace.RecordFailure(new TimeoutException("AdsPower: CDP-подключение не открылось за 15 с."));

        Assert.True(IsCdpHang(events));
        Assert.False(IsLocalApiFailure(events));
        Assert.True((bool)events[0].Properties["localApi.hasWsPuppeteer"]!);
        Assert.Equal("browser/start", events[^1].Properties["startup.lastSuccessfulLocalApi"]);
        Assert.Null(events[^1].Properties["startup.lastSuccessfulCdp"]);
        Assert.Equal("Connect", events[1].Properties["cdp.call"]);
        Assert.Equal(false, events[1].Properties["cdp.ok"]);
    }

    [Fact]
    public void Record_SwallowsThrowingSink_AndDoesNotChangeControlFlow()
    {
        using var trace = AdsPowerStartupTrace.Begin(
            "k1user",
            1,
            _ => throw new InvalidOperationException("sink failed"));
        var snapshot = AdsPowerStartupLogSanitizer.SummarizeBrowserStart(
            """{"code":0,"data":{"ws":{"puppeteer":"ws://127.0.0.1:9222/devtools/browser/abc"}}}""",
            200,
            null,
            TimeSpan.FromMilliseconds(1),
            "ws://127.0.0.1:9222/devtools/browser/abc",
            "9222",
            null);

        var local = trace.RecordLocalApi(snapshot);
        var cdp = trace.RecordCdp("Connect", "подключение CDP", TimeSpan.FromMilliseconds(1), ok: true);
        var failure = trace.RecordFailure(new TimeoutException("x"));

        Assert.NotNull(local);
        Assert.NotNull(cdp);
        Assert.NotNull(failure);
        Assert.Equal("browser/start", trace.LastSuccessfulLocalApiOperation);
        Assert.Equal("Connect", trace.LastSuccessfulCdpOperation);
    }

    [Fact]
    public async Task StartBrowserAsync_WithActiveTrace_RecordsSanitizedLocalApiEvent()
    {
        var events = new List<AdsPowerStartupDiagnosticEvent>();
        var body = $$"""
            {"code":0,"msg":"ok {{SecretCookie}}","data":{"ws":{"puppeteer":"{{SecretWs}}"},"debug_port":"9222","proxy":"{{SecretProxy}}"}}
            """;
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(body)));

        AdsPowerBrowserStartResult result;
        using (var trace = AdsPowerStartupTrace.Begin("user-1", 1, events.Add).Activate())
        {
            result = await client.StartBrowserAsync(
                new AdsPowerConnectionOptions("http://127.0.0.1:57610", "secret-key"),
                "user-1",
                "https://www.avito.ru/profile/pro/items",
                CancellationToken.None);

            Assert.Equal(SecretWs, result.WebSocketDebuggerUrl);
            Assert.Equal("browser/start", trace.LastSuccessfulLocalApiOperation);
        }

        Assert.Equal(["browser_start"], events.Select(e => e.Name).ToArray());
        var evt = events[0];
        Assert.True(evt.Ok);
        Assert.Equal("local_api", evt.Properties["startup.boundary"]);
        Assert.Equal(true, evt.Properties["localApi.hasWsPuppeteer"]);
        Assert.Equal(0, evt.Properties["localApi.adsPowerCode"]);
        Assert.Equal(200, evt.Properties["localApi.httpStatus"]);
        Assert.Equal("avito", evt.Properties["localApi.openUrlClass"]);
        Assert.Equal("user-1", evt.Properties["adsPower.userId"]);
        Assert.True((double)evt.Properties["localApi.durationMs"]! >= 0);
        AssertNoSecrets(AdsPowerStartupLogSanitizer.SerializeForInspection(evt.Properties));
        AssertNoSecrets(evt.Message);
    }

    [Fact]
    public async Task StartBrowserAsync_HttpFailure_RecordsLocalApiError_WithoutRawBody()
    {
        var events = new List<AdsPowerStartupDiagnosticEvent>();
        var body = $$"""
            {"code":-1,"msg":"proxy {{SecretProxy}} cookie {{SecretCookie}}","data":{"html":"{{SecretHtml}}"}}
            """;
        var client = BuildClient((_, _) =>
            Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.BadGateway, body)));

        using var trace = AdsPowerStartupTrace.Begin("user-1", 1, events.Add).Activate();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.StartBrowserAsync(
                new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
                "user-1",
                null,
                CancellationToken.None));

        Assert.Contains("browser/start", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretProxy, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretCookie, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretHtml, ex.Message, StringComparison.Ordinal);
        Assert.Equal(["browser_start"], events.Select(e => e.Name).ToArray());
        Assert.False(events[0].Ok);
        Assert.Equal(502, events[0].Properties["localApi.httpStatus"]);
        Assert.Equal(-1, events[0].Properties["localApi.adsPowerCode"]);
        Assert.Null(trace.LastSuccessfulLocalApiOperation);
        AssertNoSecrets(AdsPowerStartupLogSanitizer.SerializeForInspection(events[0].Properties));
        AssertNoSecrets(ex.Message);
    }

    [Fact]
    public async Task StartBrowserAsync_TransportException_RecordsType_AndRethrows()
    {
        var events = new List<AdsPowerStartupDiagnosticEvent>();
        var client = BuildClient((_, _) => throw new HttpRequestException("connection refused " + SecretProxy));

        using var trace = AdsPowerStartupTrace.Begin("user-1", 1, events.Add).Activate();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.StartBrowserAsync(
                new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
                "user-1",
                null,
                CancellationToken.None));

        Assert.Contains("connection refused", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["browser_start"], events.Select(e => e.Name).ToArray());
        Assert.False(events[0].Ok);
        Assert.Equal(typeof(HttpRequestException).FullName, events[0].Properties["localApi.transportExceptionType"]);
        Assert.Null(events[0].Properties["localApi.httpStatus"]);
        Assert.Null(trace.LastSuccessfulLocalApiOperation);
        AssertNoSecrets(AdsPowerStartupLogSanitizer.SerializeForInspection(events[0].Properties));
    }

    [Fact]
    public async Task StartBrowserAsync_DailyLimit_KeepsErrorKeyException_AndRecordsFailureSnapshot()
    {
        var events = new List<AdsPowerStartupDiagnosticEvent>();
        var body = """{"code":-1,"msg":"Exceeding open daily limit, recovery after 7 hours"}""";
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(body)));

        using var trace = AdsPowerStartupTrace.Begin("user-1", 1, events.Add).Activate();
        var ex = await Assert.ThrowsAsync<AdsPowerDailyOpenLimitExceededException>(() =>
            client.StartBrowserAsync(
                new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
                "user-1",
                null,
                CancellationToken.None));

        Assert.Equal(-1, ex.ApiCode);
        Assert.Equal("ads_power.daily_open_limit", AdsPowerDailyOpenLimitExceededException.ErrorKey);
        Assert.Equal(["browser_start"], events.Select(e => e.Name).ToArray());
        Assert.False(events[0].Ok);
        Assert.Equal(-1, events[0].Properties["localApi.adsPowerCode"]);
        Assert.False((bool)events[0].Properties["localApi.hasWsPuppeteer"]!);
        Assert.Contains("daily limit", (string)events[0].Properties["localApi.adsPowerMessage"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyIdentity_DoesNotOverwriteCallerErrorKeyFields()
    {
        using var trace = AdsPowerStartupTrace.Begin("user-1", 1);
        trace.SetStage("поиск рабочей вкладки");
        var properties = new Dictionary<string, object?>
        {
            ["error.type"] = "keep-me"
        };
        trace.CopyIdentityTo(properties);
        Assert.Equal("keep-me", properties["error.type"]);
        Assert.Equal(trace.CorrelationId, properties["startup.correlationId"]);
        Assert.Equal("поиск рабочей вкладки", properties["startup.stage"]);
    }

    private static bool IsLocalApiFailure(IReadOnlyList<AdsPowerStartupDiagnosticEvent> events)
    {
        var local = events.FirstOrDefault(e => e.Name == "browser_start");
        var failure = events.FirstOrDefault(e => e.Name == "startup_failure");
        if (local is null || failure is null)
        {
            return false;
        }

        return local.Ok == false
               && failure.Properties["startup.lastSuccessfulCdp"] is null
               && (failure.Properties["startup.lastSuccessfulLocalApi"] is null
                   || Equals(local.Properties["localApi.hasWsPuppeteer"], false));
    }

    private static bool IsCdpHang(IReadOnlyList<AdsPowerStartupDiagnosticEvent> events)
    {
        var local = events.FirstOrDefault(e => e.Name == "browser_start");
        var failure = events.FirstOrDefault(e => e.Name == "startup_failure");
        if (local is null || failure is null)
        {
            return false;
        }

        return local.Ok
               && Equals(local.Properties["localApi.hasWsPuppeteer"], true)
               && Equals(failure.Properties["startup.lastSuccessfulLocalApi"], "browser/start")
               && events.Any(e => e.Boundary == "cdp");
    }

    private static void AssertNoSecrets(string blob)
    {
        Assert.False(string.IsNullOrEmpty(blob));
        Assert.False(
            AdsPowerStartupLogSanitizer.ContainsForbiddenSecret(
                blob,
                SecretWs,
                SecretProxy,
                SecretCookie,
                SecretToken,
                SecretHtml,
                "ws-token-SECRET",
                "abc-secret-guid",
                "proxy-pass",
                "cookie-value-SECRET",
                "ads-token-SECRET",
                "SECRET-HTML",
                "ws://127.0.0.1",
                "wss://"));
        Assert.DoesNotContain("ws://127.0.0.1", blob, StringComparison.Ordinal);
        Assert.DoesNotContain("wss://", blob, StringComparison.Ordinal);
    }

    private static AdsPowerApiClient BuildClient(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> handler)
    {
        var stub = new StubHttpMessageHandler(handler);
        var factory = new StubHttpClientFactory(stub);
        return new AdsPowerApiClient(factory);
    }
}

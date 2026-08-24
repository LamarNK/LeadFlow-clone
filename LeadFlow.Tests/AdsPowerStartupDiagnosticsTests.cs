using System.Net;
using System.Net.Http;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Worker;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

[Collection("AdsPowerStartupDiagnostics")]
public sealed class AdsPowerStartupDiagnosticsTests
{
    private const string SecretWs =
        "ws://127.0.0.1:9222/devtools/browser/abc-secret-guid?token=ws-token-SECRET";
    private const string SecretProxy = "http://proxy-user:proxy-pass@10.1.2.3:8000";
    private const string SecretCookie = "sessionid=cookie-value-SECRET";
    private const string SecretToken = "Bearer ads-token-SECRET";
    private const string SecretHtml = "<html><body>full page SECRET-HTML</body></html>";
    private const string TabUrl = "https://www.avito.ru/profile/pro/items";
    private const string UrlLikeDataKey = "https://evil.example/access_token";

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
                "cookie": "{{SecretCookie}}",
                "{{UrlLikeDataKey}}": "leak-me",
                "access_token": "tok-SECRET"
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
            openUrl: TabUrl);

        Assert.True(snapshot.Ok);
        Assert.True(snapshot.HasWsPuppeteer);
        Assert.Equal(0, snapshot.AdsPowerCode);
        Assert.Equal(200, snapshot.HttpStatusCode);
        Assert.Equal("object", snapshot.DataKind);
        Assert.Equal("json", snapshot.PayloadKind);
        Assert.True(snapshot.DebugPortPresent);
        Assert.Equal("avito", snapshot.OpenUrlClass);
        Assert.Equal(842, snapshot.DurationMs, precision: 3);
        Assert.Equal(6, snapshot.DataKeyCount);
        Assert.Equal("ws,debug_port", snapshot.DataKeys);
        Assert.DoesNotContain("proxy", snapshot.DataKeys, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", snapshot.DataKeys, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access_token", snapshot.DataKeys, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", snapshot.DataKeys, StringComparison.Ordinal);
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
        Assert.DoesNotContain("avito.ru", blob, StringComparison.Ordinal);
        Assert.DoesNotContain("adsPower.openUrl", blob, StringComparison.Ordinal);
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
        Assert.DoesNotContain("10.1.2.3", blob, StringComparison.Ordinal);
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
    public void LimitText_RedactsAnyUrlToClass_NotJustCredentials()
    {
        var raw =
            $"AdsPower {SecretWs} via {SecretProxy} open {TabUrl} mail a.pakin797@gmail.com " + new string('x', 400);
        var limited = AdsPowerStartupLogSanitizer.LimitText(raw);

        Assert.True(limited.Length <= AdsPowerStartupLogSanitizer.MaxMessageLength + 1);
        Assert.DoesNotContain(SecretWs, limited, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretProxy, limited, StringComparison.Ordinal);
        Assert.DoesNotContain(TabUrl, limited, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy-pass", limited, StringComparison.Ordinal);
        Assert.DoesNotContain("10.1.2.3", limited, StringComparison.Ordinal);
        Assert.DoesNotContain("avito.ru", limited, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1:9222", limited, StringComparison.Ordinal);
        Assert.DoesNotContain("@gmail.com", limited, StringComparison.Ordinal);
        Assert.Contains("<url:other>", limited, StringComparison.Ordinal);
        Assert.Contains("<url:avito>", limited, StringComparison.Ordinal);
    }

    [Fact]
    public void Trace_CdpHang_LogsToGlobalLogger_WithSameCorrelation()
    {
        using var capture = GlobalLogCapture.Start();
        using var trace = AdsPowerStartupTrace.Begin("k1user", attempt: 2);
        using (trace.Activate())
        {
            trace.SetStage("ожидание очереди AdsPower browser/start");
            var local = AdsPowerStartupLogSanitizer.SummarizeBrowserStart(
                $$"""{"code":0,"msg":"ok","data":{"ws":{"puppeteer":"{{SecretWs}}"},"debug_port":"9222"}}""",
                200,
                null,
                TimeSpan.FromMilliseconds(410),
                SecretWs,
                "9222",
                TabUrl);
            AdsPowerStartupDiagnostics.TryLog(trace.RecordLocalApi(local));

            trace.SetStage("подключение CDP");
            AdsPowerStartupDiagnostics.TryLog(
                trace.RecordCdp("Connect", "подключение CDP", TimeSpan.FromMilliseconds(90), ok: true));

            trace.SetStage("поиск рабочей вкладки");
            AdsPowerStartupDiagnostics.TryLog(trace.RecordCdp(
                "PagesAsync",
                AdsPowerAvitoAutomationService.PageAcquireOperation,
                TimeSpan.FromMilliseconds(12),
                ok: true,
                pagesCount: 0,
                urlClasses: string.Empty));
            AdsPowerStartupDiagnostics.TryLog(trace.RecordCdp(
                "PagesAsync",
                AdsPowerAvitoAutomationService.PageAcquireRetryOperation,
                TimeSpan.FromMilliseconds(8),
                ok: true,
                pagesCount: 0,
                urlClasses: string.Empty));

            var timeout = AdsPowerAvitoAutomationService.CreateEmptyPagesAcquisitionTimeout();
            AdsPowerStartupDiagnostics.TryLog(trace.RecordFailure(timeout));
            AdsPowerStartupDiagnostics.TryLog(trace.RecordFailure(new InvalidOperationException("duplicate")));
        }

        var logged = capture.WithCorrelation(trace.CorrelationId);
        Assert.Equal(
            ["browser_start", "cdp_connect", "pages_async", "pages_async", "startup_failure"],
            logged.Select(e => (string)e.Properties["startup.event"]!).ToArray());
        Assert.All(logged, e => Assert.Equal("k1user", e.Properties["adsPower.userId"]));
        Assert.All(logged, e => Assert.Equal(2, e.Properties["startup.attempt"]));
        Assert.All(logged, e => Assert.DoesNotContain("adsPower.openUrl", e.Properties.Keys));

        var failure = logged[^1];
        Assert.Equal("поиск рабочей вкладки", failure.Properties["startup.stage"]);
        Assert.Equal("browser/start", failure.Properties["startup.lastSuccessfulLocalApi"]);
        Assert.Equal("PagesAsync", failure.Properties["startup.lastSuccessfulCdp"]);
        Assert.Equal(typeof(TimeoutException).FullName, failure.Properties["error.type"]);
        Assert.Contains("список вкладок пуст", (string)failure.Properties["error.message"]!, StringComparison.Ordinal);

        AssertNoSecrets(capture.CombinedBlob(logged));
        Assert.DoesNotContain(TabUrl, capture.CombinedBlob(logged), StringComparison.Ordinal);

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
        using var capture = GlobalLogCapture.Start();
        using var trace = AdsPowerStartupTrace.Begin("k1user", attempt: 1);
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
        AdsPowerStartupDiagnostics.TryLog(trace.RecordLocalApi(snapshot));
        AdsPowerStartupDiagnostics.TryLog(trace.RecordFailure(new HttpRequestException("timed out")));

        var logged = capture.WithCorrelation(trace.CorrelationId);
        Assert.Equal(["browser_start", "startup_failure"], logged.Select(e => (string)e.Properties["startup.event"]!).ToArray());
        Assert.False((bool)logged[0].Properties["localApi.hasWsPuppeteer"]!);
        Assert.Equal(typeof(HttpRequestException).FullName, logged[0].Properties["localApi.transportExceptionType"]);
        Assert.Null(logged[0].Properties["localApi.httpStatus"]);
        Assert.Null(logged[1].Properties["startup.lastSuccessfulLocalApi"]);
        Assert.Null(logged[1].Properties["startup.lastSuccessfulCdp"]);
        Assert.Equal("ожидание очереди AdsPower browser/start", logged[1].Properties["startup.stage"]);
        Assert.Equal("local_api", logged[0].Properties["startup.boundary"]);
        Assert.Null(logged[0].Properties["cdp.call"]);
        Assert.False(AdsPowerCdpGuard.IsCdpTimeout(new HttpRequestException("timed out")));
        Assert.True(IsLocalApiFailure(logged));
        Assert.False(IsCdpHang(logged));
    }

    [Fact]
    public void Trace_HttpSuccessWithoutWs_DoesNotMarkLocalApiSuccessful()
    {
        using var capture = GlobalLogCapture.Start();
        using var trace = AdsPowerStartupTrace.Begin("k1user", 1);
        trace.Activate();
        trace.SetStage("ожидание очереди AdsPower browser/start");
        var snapshot = AdsPowerStartupLogSanitizer.SummarizeBrowserStart(
            """{"code":0,"msg":"ok","data":{}}""",
            200,
            null,
            TimeSpan.FromMilliseconds(80),
            parsedWebSocketUrl: null,
            debugPort: null,
            openUrl: TabUrl);
        Assert.False(snapshot.Ok);
        Assert.True(snapshot.ApiSucceeded);
        Assert.False(snapshot.HasWsPuppeteer);

        AdsPowerStartupDiagnostics.TryLog(trace.RecordLocalApi(snapshot));
        AdsPowerStartupDiagnostics.TryLog(
            trace.RecordFailure(new InvalidOperationException("AdsPower не вернул ws.puppeteer endpoint.")));

        Assert.Null(trace.LastSuccessfulLocalApiOperation);
        var failure = Assert.Single(
            capture.WithCorrelation(trace.CorrelationId),
            e => Equals(e.Properties.GetValueOrDefault("startup.event"), "startup_failure"));
        Assert.Null(failure.Properties["startup.lastSuccessfulLocalApi"]);
        Assert.False((bool)capture.WithCorrelation(trace.CorrelationId)
            .Single(e => Equals(e.Properties.GetValueOrDefault("startup.event"), "browser_start"))
            .Properties["localApi.hasWsPuppeteer"]!);
        AssertNoSecrets(capture.CombinedBlob(capture.WithCorrelation(trace.CorrelationId)));
        Assert.DoesNotContain(TabUrl, capture.CombinedBlob(), StringComparison.Ordinal);
    }

    [Fact]
    public void Trace_CdpHangAfterWs_IsDistinguishableFromLocalApiFailure()
    {
        using var capture = GlobalLogCapture.Start();
        using var trace = AdsPowerStartupTrace.Begin("k1user", 1);
        trace.Activate();
        trace.SetStage("ожидание очереди AdsPower browser/start");
        AdsPowerStartupDiagnostics.TryLog(trace.RecordLocalApi(AdsPowerStartupLogSanitizer.SummarizeBrowserStart(
            """{"code":0,"data":{"ws":{"puppeteer":"ws://127.0.0.1:9222/devtools/browser/abc"}}}""",
            200,
            null,
            TimeSpan.FromMilliseconds(200),
            "ws://127.0.0.1:9222/devtools/browser/abc",
            "9222",
            null)));
        trace.SetStage("подключение CDP");
        AdsPowerStartupDiagnostics.TryLog(
            trace.RecordCdp("Connect", "подключение CDP", TimeSpan.FromSeconds(15), ok: false));
        AdsPowerStartupDiagnostics.TryLog(
            trace.RecordFailure(new TimeoutException("AdsPower: CDP-подключение не открылось за 15 с.")));

        var logged = capture.WithCorrelation(trace.CorrelationId);
        Assert.True(IsCdpHang(logged));
        Assert.False(IsLocalApiFailure(logged));
        Assert.True((bool)logged[0].Properties["localApi.hasWsPuppeteer"]!);
        Assert.Equal("browser/start", logged[^1].Properties["startup.lastSuccessfulLocalApi"]);
        Assert.Null(logged[^1].Properties["startup.lastSuccessfulCdp"]);
        Assert.Equal("Connect", logged[1].Properties["cdp.call"]);
        Assert.Equal(false, logged[1].Properties["cdp.ok"]);
    }

    [Fact]
    public void RecordFailure_InnerCancellation_DoesNotOccupySlotForOuterTimeout()
    {
        using var capture = GlobalLogCapture.Start();
        using var trace = AdsPowerStartupTrace.Begin("k1user", 1);
        trace.Activate();
        trace.SetStage("поиск рабочей вкладки");
        AdsPowerStartupDiagnostics.TryLog(trace.RecordFailure(new OperationCanceledException()));
        AdsPowerStartupDiagnostics.TryLog(trace.RecordFailure(new TaskCanceledException("inner cancel")));

        var timeout = new TimeoutException("AdsPower: запуск сессии не завершился за 3 мин.");
        AdsPowerStartupDiagnostics.TryLog(trace.RecordFailure(timeout));
        AdsPowerStartupDiagnostics.TryLog(trace.RecordFailure(new InvalidOperationException("duplicate")));

        var failures = capture.WithCorrelation(trace.CorrelationId)
            .Where(e => Equals(e.Properties.GetValueOrDefault("startup.event"), "startup_failure"))
            .ToList();
        var failure = Assert.Single(failures);
        Assert.Equal(typeof(TimeoutException).FullName, failure.Properties["error.type"]);
        Assert.Contains("3 мин", (string)failure.Properties["error.message"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationCanceled", (string)failure.Properties["error.type"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskCanceled", (string)failure.Properties["error.type"]!, StringComparison.Ordinal);
        Assert.Equal("поиск рабочей вкладки", failure.Properties["startup.stage"]);
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
    public async Task StartBrowserAsync_WithActiveTrace_WritesSanitizedRecordsToGlobalLogger()
    {
        using var capture = GlobalLogCapture.Start();
        var body = $$"""
            {"code":0,"msg":"ok {{SecretCookie}}","data":{"ws":{"puppeteer":"{{SecretWs}}"},"debug_port":"9222","proxy":"{{SecretProxy}}","{{UrlLikeDataKey}}":"leak"}}
            """;
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(body)));

        AdsPowerBrowserStartResult result;
        using (var trace = AdsPowerStartupTrace.Begin("user-1", 1).Activate())
        {
            result = await client.StartBrowserAsync(
                new AdsPowerConnectionOptions("http://127.0.0.1:57610", "secret-key"),
                "user-1",
                TabUrl,
                CancellationToken.None);

            Assert.Equal(SecretWs, result.WebSocketDebuggerUrl);
            Assert.Equal("browser/start", trace.LastSuccessfulLocalApiOperation);

            var correlated = capture.WithCorrelation(trace.CorrelationId);
            Assert.Contains(correlated, e => e.Message.Contains("request started", StringComparison.Ordinal));
            var start = Assert.Single(correlated, e => Equals(e.Properties.GetValueOrDefault("startup.event"), "browser_start"));
            Assert.True((bool)start.Properties["localApi.hasWsPuppeteer"]!);
            Assert.Equal(0, start.Properties["localApi.adsPowerCode"]);
            Assert.Equal(200, start.Properties["localApi.httpStatus"]);
            Assert.Equal("avito", start.Properties["localApi.openUrlClass"]);
            Assert.Equal("avito", start.Properties["adsPower.openUrlClass"]);
            Assert.True((double)start.Properties["localApi.durationMs"]! >= 0);
            Assert.Equal("ws,debug_port", start.Properties["localApi.dataKeys"]);
            AssertLoggedNoRawOpenUrl(correlated);
            AssertNoSecrets(capture.CombinedBlob(correlated));
            Assert.DoesNotContain(TabUrl, capture.CombinedBlob(correlated), StringComparison.Ordinal);
            Assert.DoesNotContain("evil.example", capture.CombinedBlob(correlated), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StartBrowserAsync_HttpFailure_RecordsLocalApiError_WithoutRawBody()
    {
        using var capture = GlobalLogCapture.Start();
        var body = $$"""
            {"code":-1,"msg":"proxy {{SecretProxy}} cookie {{SecretCookie}} open {{TabUrl}}","data":{"html":"{{SecretHtml}}"}}
            """;
        var client = BuildClient((_, _) =>
            Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.BadGateway, body)));

        using var trace = AdsPowerStartupTrace.Begin("user-1", 1).Activate();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.StartBrowserAsync(
                new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
                "user-1",
                TabUrl,
                CancellationToken.None));

        Assert.Contains("browser/start", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretProxy, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TabUrl, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("10.1.2.3", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("avito.ru", ex.Message, StringComparison.Ordinal);

        var start = Assert.Single(capture.WithCorrelation(trace.CorrelationId),
            e => Equals(e.Properties.GetValueOrDefault("startup.event"), "browser_start"));
        Assert.False((bool)start.Properties["localApi.ok"]!);
        Assert.Equal(502, start.Properties["localApi.httpStatus"]);
        Assert.Equal(-1, start.Properties["localApi.adsPowerCode"]);
        Assert.Null(trace.LastSuccessfulLocalApiOperation);
        AssertLoggedNoRawOpenUrl(capture.WithCorrelation(trace.CorrelationId));
        AssertNoSecrets(capture.CombinedBlob(capture.WithCorrelation(trace.CorrelationId)));
        AssertNoSecrets(ex.Message);
    }

    [Fact]
    public async Task StartBrowserAsync_TransportException_RecordsType_AndRethrows()
    {
        using var capture = GlobalLogCapture.Start();
        var client = BuildClient((_, _) => throw new HttpRequestException("connection refused " + SecretProxy));

        using var trace = AdsPowerStartupTrace.Begin("user-1", 1).Activate();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.StartBrowserAsync(
                new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
                "user-1",
                null,
                CancellationToken.None));

        Assert.Contains("connection refused", ex.Message, StringComparison.Ordinal);
        var start = Assert.Single(
            capture.WithCorrelation(trace.CorrelationId),
            e => Equals(e.Properties.GetValueOrDefault("startup.event"), "browser_start"));
        Assert.False((bool)start.Properties["localApi.ok"]!);
        Assert.Equal(typeof(HttpRequestException).FullName, start.Properties["localApi.transportExceptionType"]);
        Assert.Null(start.Properties["localApi.httpStatus"]);
        Assert.Null(trace.LastSuccessfulLocalApiOperation);
        Assert.True((double)start.Properties["localApi.durationMs"]! >= 0);
        AssertNoSecrets(capture.CombinedBlob(capture.WithCorrelation(trace.CorrelationId)));
    }

    [Fact]
    public async Task StartBrowserAsync_Cancelled_LogsCallDuration_BeforeRethrow()
    {
        using var capture = GlobalLogCapture.Start();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = BuildClient(async (_, _) =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30));
            return StubHttpMessageHandler.Ok("""{"code":0,"data":{}}""");
        });

        using var trace = AdsPowerStartupTrace.Begin("user-1", 1).Activate();
        var pending = client.StartBrowserAsync(
            new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
            "user-1",
            TabUrl,
            cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        watch.Stop();

        var start = Assert.Single(
            capture.WithCorrelation(trace.CorrelationId),
            e => Equals(e.Properties.GetValueOrDefault("startup.event"), "browser_start"));
        Assert.False((bool)start.Properties["localApi.ok"]!);
        Assert.Contains(
            "Canceled",
            (string)start.Properties["localApi.transportExceptionType"]!,
            StringComparison.Ordinal);
        Assert.True((double)start.Properties["localApi.durationMs"]! >= 0);
        Assert.True((double)start.Properties["localApi.durationMs"]! < watch.Elapsed.TotalMilliseconds + 5000);
        Assert.Null(trace.LastSuccessfulLocalApiOperation);
        Assert.DoesNotContain(
            capture.WithCorrelation(trace.CorrelationId),
            e => Equals(e.Properties.GetValueOrDefault("startup.event"), "startup_failure"));
        AssertLoggedNoRawOpenUrl(capture.WithCorrelation(trace.CorrelationId));
        Assert.DoesNotContain(TabUrl, capture.CombinedBlob(capture.WithCorrelation(trace.CorrelationId)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartBrowserAsync_DailyLimit_KeepsErrorKeyException_AndRecordsFailureSnapshot()
    {
        using var capture = GlobalLogCapture.Start();
        var body = """{"code":-1,"msg":"Exceeding open daily limit, recovery after 7 hours"}""";
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(body)));

        using var trace = AdsPowerStartupTrace.Begin("user-1", 1).Activate();
        var ex = await Assert.ThrowsAsync<AdsPowerDailyOpenLimitExceededException>(() =>
            client.StartBrowserAsync(
                new AdsPowerConnectionOptions("http://127.0.0.1:57610", null),
                "user-1",
                null,
                CancellationToken.None));

        Assert.Equal(-1, ex.ApiCode);
        Assert.Equal("ads_power.daily_open_limit", AdsPowerDailyOpenLimitExceededException.ErrorKey);
        var start = Assert.Single(
            capture.WithCorrelation(trace.CorrelationId),
            e => Equals(e.Properties.GetValueOrDefault("startup.event"), "browser_start"));
        Assert.False((bool)start.Properties["localApi.ok"]!);
        Assert.Equal(-1, start.Properties["localApi.adsPowerCode"]);
        Assert.False((bool)start.Properties["localApi.hasWsPuppeteer"]!);
        Assert.Contains(
            "daily limit",
            (string)start.Properties["localApi.adsPowerMessage"]!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(capture.Entries, e => e.ErrorKey == AdsPowerDailyOpenLimitExceededException.ErrorKey);
        Assert.Null(trace.LastSuccessfulLocalApiOperation);
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
        Assert.False(properties.ContainsKey("adsPower.openUrl"));
    }

    private static bool IsLocalApiFailure(IReadOnlyList<CapturedGlobalLog> events)
    {
        var local = events.FirstOrDefault(e => Equals(e.Properties.GetValueOrDefault("startup.event"), "browser_start"));
        var failure = events.FirstOrDefault(e => Equals(e.Properties.GetValueOrDefault("startup.event"), "startup_failure"));
        if (local is null || failure is null)
        {
            return false;
        }

        return Equals(local.Properties.GetValueOrDefault("localApi.ok"), false)
               && failure.Properties["startup.lastSuccessfulCdp"] is null
               && (failure.Properties["startup.lastSuccessfulLocalApi"] is null
                   || Equals(local.Properties["localApi.hasWsPuppeteer"], false));
    }

    private static bool IsCdpHang(IReadOnlyList<CapturedGlobalLog> events)
    {
        var local = events.FirstOrDefault(e => Equals(e.Properties.GetValueOrDefault("startup.event"), "browser_start"));
        var failure = events.FirstOrDefault(e => Equals(e.Properties.GetValueOrDefault("startup.event"), "startup_failure"));
        if (local is null || failure is null)
        {
            return false;
        }

        return Equals(local.Properties.GetValueOrDefault("localApi.ok"), true)
               && Equals(local.Properties["localApi.hasWsPuppeteer"], true)
               && Equals(failure.Properties["startup.lastSuccessfulLocalApi"], "browser/start")
               && events.Any(e => Equals(e.Properties.GetValueOrDefault("startup.boundary"), "cdp"));
    }

    private static void AssertLoggedNoRawOpenUrl(IEnumerable<CapturedGlobalLog> logs)
    {
        foreach (var log in logs)
        {
            Assert.False(log.Properties.ContainsKey("adsPower.openUrl"));
            Assert.DoesNotContain(TabUrl, log.Message, StringComparison.Ordinal);
            foreach (var value in log.Properties.Values)
            {
                if (value is string text)
                {
                    Assert.DoesNotContain(TabUrl, text, StringComparison.Ordinal);
                    Assert.DoesNotContain("avito.ru", text, StringComparison.Ordinal);
                }
            }
        }
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
                TabUrl,
                UrlLikeDataKey,
                "ws-token-SECRET",
                "abc-secret-guid",
                "proxy-pass",
                "cookie-value-SECRET",
                "ads-token-SECRET",
                "SECRET-HTML",
                "ws://127.0.0.1",
                "wss://",
                "10.1.2.3",
                "avito.ru"));
        Assert.DoesNotContain("adsPower.openUrl\":", blob, StringComparison.Ordinal);
        Assert.DoesNotContain("\"adsPower.openUrl\"", blob, StringComparison.Ordinal);
    }

    private static AdsPowerApiClient BuildClient(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> handler)
    {
        var stub = new StubHttpMessageHandler(handler);
        var factory = new StubHttpClientFactory(stub);
        return new AdsPowerApiClient(factory);
    }
}

[CollectionDefinition("AdsPowerStartupDiagnostics", DisableParallelization = true)]
public sealed class AdsPowerStartupDiagnosticsCollection;

using System.Diagnostics;
using System.Net.Http;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Tests.Support;
using Xunit;

namespace LeadFlow.Tests;

[Collection("AdsPowerLocalApiThrottle")]
public sealed class AdsPowerLocalApiThrottleTests
{
    private const string ProxyBody = """
        {"code":0,"data":{"list":[{
          "user_id":"profile-1",
          "user_proxy_config":{
            "proxy_type":"http","proxy_host":"203.0.113.10","proxy_port":"8080",
            "proxy_user":"proxy-user","proxy_password":"proxy-pass-SECRET"
          }
        }]}}
        """;

    [Fact]
    public void UserListUserIdQuery_MatchesOfficialPostmanArrayContract()
    {
        var encoded = AdsPowerApiClient.FormatUserListUserIdQuery("abcd01");
        var decoded = Uri.UnescapeDataString(encoded);
        Assert.Equal("""["abcd01"]""", decoded);
        Assert.StartsWith("%5B", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoUserListCalls_AreNotSentFasterThanOnePerSecond()
    {
        var sentAt = new List<long>();
        var client = BuildClient((request, _) =>
        {
            sentAt.Add(Stopwatch.GetTimestamp());
            return Task.FromResult(StubHttpMessageHandler.Ok(ProxyBody));
        });
        var options = UniqueOptions();

        await client.GetProfileProxyAsync(options, "profile-1", CancellationToken.None);
        await client.GetProfileProxyAsync(options, "profile-1", CancellationToken.None);

        Assert.Equal(2, sentAt.Count);
        var gapMs = (sentAt[1] - sentAt[0]) * 1000.0 / Stopwatch.Frequency;
        Assert.True(gapMs >= AdsPowerApiThrottler.UserListMinIntervalMs - 30, $"gap {gapMs:F1} ms");
    }

    [Fact]
    public async Task QueueTimeout_DoesNotSendHttp()
    {
        var sent = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = UniqueOptions();

        var first = AdsPowerApiThrottler.ExecuteAsync(
            options.BaseUrl,
            async ct =>
            {
                firstEntered.TrySetResult();
                await release.Task.WaitAsync(ct);
                Interlocked.Increment(ref sent);
                return 1;
            },
            CancellationToken.None,
            new AdsPowerThrottleOptions { Operation = "user/list", MinIntervalMs = 0 });

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondSent = false;
        var second = AdsPowerApiThrottler.ExecuteAsync(
            options.BaseUrl,
            _ =>
            {
                secondSent = true;
                return Task.FromResult(2);
            },
            CancellationToken.None,
            new AdsPowerThrottleOptions
            {
                Operation = "user/list",
                MinIntervalMs = 0,
                QueueWaitTimeout = TimeSpan.FromMilliseconds(150)
            });

        var ex = await Assert.ThrowsAsync<AdsPowerLocalApiTimeoutException>(() => second);
        Assert.Equal("queue_wait", ex.Phase);
        Assert.False(secondSent);

        release.TrySetResult();
        Assert.Equal(1, await first);
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task HttpTimeout_ReleasesQueueForNextCaller()
    {
        var options = UniqueOptions();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var hung = AdsPowerApiThrottler.ExecuteAsync(
            options.BaseUrl,
            async ct =>
            {
                firstEntered.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return 1;
            },
            CancellationToken.None,
            new AdsPowerThrottleOptions
            {
                Operation = "user/list",
                MinIntervalMs = 0,
                HttpTimeout = TimeSpan.FromMilliseconds(150)
            });

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var timeout = await Assert.ThrowsAsync<AdsPowerLocalApiTimeoutException>(() => hung);
        Assert.Equal("http_response", timeout.Phase);

        var secondWatch = Stopwatch.StartNew();
        var second = await AdsPowerApiThrottler.ExecuteAsync(
            options.BaseUrl,
            _ => Task.FromResult(2),
            CancellationToken.None,
            new AdsPowerThrottleOptions { Operation = "user/list", MinIntervalMs = 0 });
        secondWatch.Stop();

        Assert.Equal(2, second);
        Assert.True(secondWatch.Elapsed < TimeSpan.FromSeconds(2), $"gate held {secondWatch.Elapsed}");
    }

    [Fact]
    public async Task CancelledStartup_DoesNotRunDeferredLocalApiAction()
    {
        var options = UniqueOptions();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = AdsPowerApiThrottler.ExecuteAsync(
            options.BaseUrl,
            async ct =>
            {
                firstEntered.TrySetResult();
                await release.Task.WaitAsync(CancellationToken.None);
                return 1;
            },
            CancellationToken.None,
            new AdsPowerThrottleOptions { Operation = "browser/start", MinIntervalMs = 0 });

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        var deferredRan = false;
        var pending = AdsPowerApiThrottler.ExecuteAsync(
            options.BaseUrl,
            _ =>
            {
                deferredRan = true;
                return Task.FromResult(2);
            },
            cts.Token,
            new AdsPowerThrottleOptions { Operation = "browser/start", MinIntervalMs = 0 });

        await Task.Delay(80);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(deferredRan);

        release.TrySetResult();
        Assert.Equal(1, await first);
        Assert.False(deferredRan);
    }

    [Fact]
    public async Task GetProfileProxyAsync_Http200ApiCodeNotZero_ThrowsAndLogsSafeOutcome()
    {
        using var capture = GlobalLogCapture.Start();
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(
            """{"code":-1,"msg":"failed proxy http://proxy-user:proxy-pass-SECRET@10.1.2.3:8000"}""")));
        var options = UniqueOptions();
        using var trace = AdsPowerStartupTrace.Begin("profile-1", 1).Activate();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetProfileProxyAsync(options, "profile-1", CancellationToken.None));

        Assert.Contains("code -1", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy-pass-SECRET", ex.Message, StringComparison.Ordinal);
        var logs = capture.WithCorrelation(trace.CorrelationId);
        Assert.Contains(logs, e => Equals(e.Properties.GetValueOrDefault("localApi.operation"), "user/list"));
        Assert.Contains(logs, e => Equals(e.Properties.GetValueOrDefault("localApi.outcome"), "error"));
        var blob = capture.CombinedBlob(logs);
        Assert.DoesNotContain("proxy-pass-SECRET", blob, StringComparison.Ordinal);
        Assert.DoesNotContain("10.1.2.3", blob, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartBrowserAsync_Http200WithoutPuppeteer_IsError()
    {
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(
            """{"code":0,"msg":"success","data":{}}""")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.StartBrowserAsync(UniqueOptions(), "user-1", null, CancellationToken.None));

        Assert.Contains("ws.puppeteer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProfileProxyAsync_DoesNotLogProxySecrets()
    {
        using var capture = GlobalLogCapture.Start();
        var client = BuildClient((_, _) => Task.FromResult(StubHttpMessageHandler.Ok(ProxyBody)));
        using var trace = AdsPowerStartupTrace.Begin("profile-1", 1).Activate();

        var proxy = await client.GetProfileProxyAsync(UniqueOptions(), "profile-1", CancellationToken.None);

        Assert.Equal("proxy-pass-SECRET", proxy!.Password);
        var blob = capture.CombinedBlob(capture.WithCorrelation(trace.CorrelationId));
        Assert.DoesNotContain("proxy-pass-SECRET", blob, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.10", blob, StringComparison.Ordinal);
        var log = Assert.Single(
            capture.WithCorrelation(trace.CorrelationId),
            e => Equals(e.Properties.GetValueOrDefault("startup.event"), "user_list"));
        Assert.Equal("ok", log.Properties["localApi.outcome"]);
        Assert.Equal("user/list", log.Properties["localApi.operation"]);
        Assert.True(Convert.ToDouble(log.Properties["localApi.queueWaitMs"]) >= 0);
        Assert.Equal(200, Convert.ToInt32(log.Properties["localApi.httpStatus"]));
    }

    private static AdsPowerConnectionOptions UniqueOptions() =>
        new($"http://127.0.0.1:{Random.Shared.Next(20000, 60000)}/{Guid.NewGuid():N}", null);

    private static AdsPowerApiClient BuildClient(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> handler)
    {
        var stub = new StubHttpMessageHandler(handler);
        return new AdsPowerApiClient(new StubHttpClientFactory(stub));
    }
}

[CollectionDefinition("AdsPowerLocalApiThrottle", DisableParallelization = true)]
public sealed class AdsPowerLocalApiThrottleCollection;

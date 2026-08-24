using System.Collections.Concurrent;
using System.Diagnostics;

namespace LeadFlow.Core.Services.AdsPower;

internal sealed class AdsPowerThrottleOptions
{
    public static AdsPowerThrottleOptions Default { get; } = new();

    public static AdsPowerThrottleOptions UserList { get; } = new()
    {
        Operation = AdsPowerApiThrottler.UserListOperation,
        MinIntervalMs = AdsPowerApiThrottler.UserListMinIntervalMs,
        HttpTimeout = AdsPowerApiThrottler.UserListHttpTimeout
    };

    public static AdsPowerThrottleOptions GroupList { get; } = new()
    {
        Operation = "group/list",
        MinIntervalMs = AdsPowerApiThrottler.UserListMinIntervalMs,
        HttpTimeout = AdsPowerApiThrottler.UserListHttpTimeout
    };

    public static AdsPowerThrottleOptions BrowserStop { get; } = new()
    {
        Operation = "browser/stop",
        HttpTimeout = AdsPowerApiThrottler.BrowserStopHttpTimeout
    };

    public string Operation { get; init; } = "local_api";

    public int MinIntervalMs { get; init; } = AdsPowerApiThrottler.MinIntervalMs;

    public TimeSpan QueueWaitTimeout { get; init; } = AdsPowerApiThrottler.DefaultQueueWaitTimeout;

    public TimeSpan? HttpTimeout { get; init; }
}

internal sealed class AdsPowerThrottleCall
{
    public TimeSpan QueueWait { get; set; }

    public TimeSpan RateLimitWait { get; set; }

    public bool Sent { get; set; }
}

/// <summary>
/// Сериализует запросы к одному экземпляру Local API AdsPower.
/// Интервал считается от фактической отправки, а не от успешного ответа.
/// Semaphore удерживается на время HTTP, но HTTP/очередь имеют короткие deadline,
/// чтобы зависший user/list не держал очередь до внешнего 180-секундного timeout.
/// </summary>
internal static class AdsPowerApiThrottler
{
    internal const string UserListOperation = "user/list";

    /// <summary>Общий запас для browser/start и прочих вызовов.</summary>
    internal const int MinIntervalMs = 1200;

    /// <summary>GET /api/v1/user/list и GET /api/v1/group/list — 1 фактически отправленный запрос в секунду.</summary>
    internal const int UserListMinIntervalMs = 1000;

    internal static readonly TimeSpan DefaultQueueWaitTimeout = TimeSpan.FromSeconds(45);

    internal static readonly TimeSpan UserListHttpTimeout = TimeSpan.FromSeconds(8);

    internal static readonly TimeSpan BrowserStopHttpTimeout = TimeSpan.FromSeconds(8);

    internal static TimeSpan? QueueWaitTimeoutOverride { get; set; }

    internal static TimeSpan? HttpTimeoutOverride { get; set; }

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, long> LastRequestTicks =
        new(StringComparer.OrdinalIgnoreCase);

    public static Task<T> ExecuteAsync<T>(
        string baseUrl,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default,
        AdsPowerThrottleOptions? options = null,
        AdsPowerThrottleCall? call = null) =>
        ExecuteAsync(baseUrl, (_, ct) => action(ct), cancellationToken, options, call);

    public static async Task<T> ExecuteAsync<T>(
        string baseUrl,
        Func<AdsPowerThrottleCall, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default,
        AdsPowerThrottleOptions? options = null,
        AdsPowerThrottleCall? call = null)
    {
        options ??= AdsPowerThrottleOptions.Default;
        call ??= new AdsPowerThrottleCall();
        var key = NormalizeKey(baseUrl);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        var queueTimeout = QueueWaitTimeoutOverride ?? options.QueueWaitTimeout;
        var httpTimeout = HttpTimeoutOverride ?? options.HttpTimeout;
        var queueWatch = Stopwatch.StartNew();

        using var queueCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (queueTimeout > TimeSpan.Zero)
        {
            queueCts.CancelAfter(queueTimeout);
        }

        try
        {
            await gate.WaitAsync(queueCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            call.QueueWait = queueWatch.Elapsed;
            throw new AdsPowerLocalApiTimeoutException(
                options.Operation,
                "queue_wait",
                call.QueueWait,
                queueWatch.Elapsed);
        }

        call.QueueWait = queueWatch.Elapsed;
        var rateWatch = Stopwatch.StartNew();
        try
        {
            // Отменённый старт не должен получить gate и выполнить browser/start / user/list.
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForSlotAsync(key, options.MinIntervalMs, cancellationToken).ConfigureAwait(false);
            call.RateLimitWait = rateWatch.Elapsed;
            cancellationToken.ThrowIfCancellationRequested();

            // Интервал от фактической отправки, даже если HTTP потом упадёт или зависнет.
            RecordRequest(key);
            call.Sent = true;

            using var httpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (httpTimeout is { } ht && ht > TimeSpan.Zero)
            {
                httpCts.CancelAfter(ht);
            }

            try
            {
                return await action(call, httpCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AdsPowerLocalApiTimeoutException(
                    options.Operation,
                    "http_response",
                    call.QueueWait,
                    rateWatch.Elapsed);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal static void ResetForTests()
    {
        QueueWaitTimeoutOverride = null;
        HttpTimeoutOverride = null;
        LastRequestTicks.Clear();
        foreach (var pair in Gates.ToArray())
        {
            if (Gates.TryRemove(pair.Key, out var gate))
            {
                try
                {
                    if (gate.CurrentCount == 0)
                    {
                        gate.Release();
                    }
                }
                catch (SemaphoreFullException)
                {
                    // already released
                }

                gate.Dispose();
            }
        }
    }

    private static async Task WaitForSlotAsync(string key, int minIntervalMs, CancellationToken cancellationToken)
    {
        if (!LastRequestTicks.TryGetValue(key, out var lastTicks) || minIntervalMs <= 0)
        {
            return;
        }

        var elapsedMs = (Stopwatch.GetTimestamp() - lastTicks) * 1000.0 / Stopwatch.Frequency;
        var waitMs = (int)Math.Ceiling(minIntervalMs - elapsedMs);
        if (waitMs > 0)
        {
            await Task.Delay(waitMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RecordRequest(string key) =>
        LastRequestTicks[key] = Stopwatch.GetTimestamp();

    private static string NormalizeKey(string baseUrl) =>
        baseUrl.Trim().TrimEnd('/').ToLowerInvariant();
}

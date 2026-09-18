using System.Net.Http.Headers;

namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Дешёвая предполётная проверка доступа воркера в интернет перед открытием браузера:
/// если машина воркера без сети, проход по любому аккаунту заведомо упадёт после
/// открытия AdsPower/Chrome (расход лимитов открытий и времени). Проверяет только сеть
/// машины воркера; работоспособность прокси конкретного аккаунта определяется на
/// навигации классификатором <see cref="Avito.AvitoNetworkErrorClassifier"/>.
/// Любой HTTP-ответ (включая 4xx/5xx) считается признаком живой сети.
/// </summary>
internal static class WorkerConnectivityProbe
{
    private const string ProbeUrl = "https://www.avito.ru";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Время жизни положительного результата: положительный цикл не перепроверяет сеть.</summary>
    private static readonly TimeSpan SuccessCacheLifetime = TimeSpan.FromMinutes(3);

    /// <summary>Время жизни отрицательного результата: без сети перепроверяем раз в ~минуту.</summary>
    private static readonly TimeSpan FailureCacheLifetime = TimeSpan.FromSeconds(45);

    /// <summary>На сколько откладываются проходы всех аккаунтов при недоступной сети.</summary>
    public static readonly TimeSpan OfflineRetryAfter = TimeSpan.FromMinutes(5);

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim ProbeLock = new(1, 1);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = ProbeTimeout
    });

    private static bool _hasCached;
    private static bool _cachedOnline;
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    static WorkerConnectivityProbe() => Http.Timeout = ProbeTimeout;

    /// <summary>
    /// True, если сеть воркера жива. Результат кэшируется: успех — 3 мин, неудача — 45 с,
    /// чтобы цикл мониторинга (тикающий каждые пару секунд) не устраивал HTTP-шторм.
    /// </summary>
    public static async Task<bool> IsOnlineAsync(CancellationToken cancellationToken)
    {
        lock (Gate)
        {
            if (_hasCached)
            {
                var lifetime = _cachedOnline ? SuccessCacheLifetime : FailureCacheLifetime;
                if (DateTime.UtcNow - _cachedAtUtc < lifetime)
                {
                    return _cachedOnline;
                }
            }
        }

        await ProbeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (Gate)
            {
                if (_hasCached)
                {
                    var lifetime = _cachedOnline ? SuccessCacheLifetime : FailureCacheLifetime;
                    if (DateTime.UtcNow - _cachedAtUtc < lifetime)
                    {
                        return _cachedOnline;
                    }
                }
            }

            var online = await ProbeOnceAsync(cancellationToken).ConfigureAwait(false);
            lock (Gate)
            {
                _hasCached = true;
                _cachedOnline = online;
                _cachedAtUtc = DateTime.UtcNow;
            }

            return online;
        }
        finally
        {
            ProbeLock.Release();
        }
    }

    /// <summary>Сбрасывает кэш (например, после фиксации сетевого сбоя в проходе).</summary>
    public static void InvalidateCache()
    {
        lock (Gate)
        {
            _hasCached = false;
        }
    }

    private static async Task<bool> ProbeOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Head, ProbeUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LeadFlowWorker", "1.0"));
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            // Любой ответ сервера (включая 4xx/5xx) доказывает, что DNS+TCP+TLS работают.
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;

namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Сериализует запросы к одному экземпляру Local API AdsPower и выдерживает минимальный интервал между ними.
/// AdsPower отвечает <c>Too many request per second</c>, если слать параллельно много browser/start.
/// </summary>
internal static class AdsPowerApiThrottler
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, long> LastRequestTicks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>AdsPower Local API допускает примерно 1 запрос/с; берём запас.</summary>
    private const int MinIntervalMs = 1200;

    public static async Task<T> ExecuteAsync<T>(
        string baseUrl,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        var key = NormalizeKey(baseUrl);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WaitForSlotAsync(key, cancellationToken).ConfigureAwait(false);
            var result = await action(cancellationToken).ConfigureAwait(false);
            RecordRequest(key);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task WaitForSlotAsync(string key, CancellationToken cancellationToken)
    {
        if (!LastRequestTicks.TryGetValue(key, out var lastTicks))
        {
            return;
        }

        var elapsedMs = (Stopwatch.GetTimestamp() - lastTicks) * 1000.0 / Stopwatch.Frequency;
        var waitMs = (int)Math.Ceiling(MinIntervalMs - elapsedMs);
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
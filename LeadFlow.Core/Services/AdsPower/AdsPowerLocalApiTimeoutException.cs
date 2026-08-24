namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Ограниченный deadline Local API (очередь / HTTP) истек раньше внешнего startup timeout.
/// Retryable: не daily-limit / in-use / rate-limit.
/// </summary>
public sealed class AdsPowerLocalApiTimeoutException : TimeoutException
{
    public const string ErrorKey = "ads_power.local_api_timeout";

    public AdsPowerLocalApiTimeoutException(
        string operation,
        string phase,
        TimeSpan queueWait,
        TimeSpan elapsed)
        : base(FormatMessage(operation, phase, queueWait, elapsed))
    {
        Operation = operation;
        Phase = phase;
        QueueWait = queueWait;
        Duration = elapsed;
    }

    public string Operation { get; }

    public string Phase { get; }

    public TimeSpan QueueWait { get; }

    public TimeSpan Duration { get; }

    public static AdsPowerLocalApiTimeoutException? Find(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AdsPowerLocalApiTimeoutException timeout)
            {
                return timeout;
            }
        }

        return null;
    }

    private static string FormatMessage(string operation, string phase, TimeSpan queueWait, TimeSpan elapsed) =>
        $"AdsPower Local API timeout: {operation} {phase} " +
        $"(queue {queueWait.TotalMilliseconds:F0} ms, elapsed {elapsed.TotalMilliseconds:F0} ms).";
}

using LeadFlow.Core.Services.AdsPower;

namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Короткий повтор прохода после переходного сбоя AdsPower (CDP hang или Local API queue/HTTP timeout).
/// Ночной пол 45–90 мин к этому RetryAfter не применяется.
/// </summary>
internal static class WorkerAdsPowerPassRetry
{
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(1);

    public static TimeSpan? FromException(Exception exception)
    {
        if (AdsPowerCdpGuard.FindCdpTimeout(exception) is not null)
        {
            return Delay;
        }

        if (AdsPowerLocalApiTimeoutException.Find(exception) is not null)
        {
            return Delay;
        }

        return null;
    }

    public static string EventType(Exception exception) =>
        FromException(exception) is null ? "Error" : "Warning";
}

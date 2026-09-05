using LeadFlow.Core.Services.AdsPower;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Короткий повтор прохода после переходного сбоя (CDP hang, Local API timeout,
/// занятый UserDataDir обычного Chrome). Ночной пол 45–90 мин к RetryAfter не применяется.
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

        if (LocalChromeLaunchDiagnostics.IsProfileBusy(exception))
        {
            return Delay;
        }

        return null;
    }

    public static bool IsLocalChromeProfileBusy(Exception exception) =>
        LocalChromeLaunchDiagnostics.IsProfileBusy(exception);

    public static bool IsLocalChromeProfileBusy(string? text) =>
        LocalChromeLaunchDiagnostics.IsProfileBusy(text);

    public static string EventType(Exception exception) =>
        FromException(exception) is null ? "Error" : "Warning";
}

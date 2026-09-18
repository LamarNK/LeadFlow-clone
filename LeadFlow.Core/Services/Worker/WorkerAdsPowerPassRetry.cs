using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Короткий повтор прохода после переходного сбоя (CDP hang, Local API timeout,
/// занятый UserDataDir обычного Chrome, сетевой сбой). Ночной пол 45–90 мин к RetryAfter
/// не применяется. Повторяющиеся подряд переходные сбои удлиняют паузу (backoff,
/// <see cref="Escalate"/>): лежащий прокси/интернет не должен долбить Avito каждую минуту.
/// </summary>
internal static class WorkerAdsPowerPassRetry
{
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(1);

    /// <summary>Пауза после терминального сетевого сбоя (нет интернета/DNS/прокси).</summary>
    public static readonly TimeSpan NetworkDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Кулдаун петли «капча → решение → reload → капча»: немедленный повтор только усиливает
    /// подозрения Avito к IP. Поднимается ночным полом (см. WorkerAccountPassDelay).
    /// </summary>
    public static readonly TimeSpan CaptchaLoopCooldownDelay = TimeSpan.FromMinutes(15);

    /// <summary>Потолок backoff-паузы переходных сбоев (базовая пауза не укорачивается кривой).</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(10);

    public static TimeSpan? FromException(Exception exception)
    {
        if (Find<AvitoSessionDeadException>(exception) is not null)
        {
            return Delay;
        }

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

        if (Find<AvitoRestartBudgetExhaustedException>(exception) is { } restartBudgetEx)
        {
            return restartBudgetEx.LooksLikeCaptchaLoop ? CaptchaLoopCooldownDelay : Delay;
        }

        if (AvitoNetworkErrorClassifier.Classify(exception) is { } kind
            and not AvitoNetworkErrorKind.None)
        {
            // ProxyFailure здесь — недоконвертированный токен навигации (обычно он превращается
            // в AdsPowerProxyFailureException с собственной веткой обработки). Даём сетевую паузу:
            // статус аккаунта остаётся Authorized, не Error.
            return NetworkDelay;
        }

        return null;
    }

    /// <summary>
    /// Ищет исключение типа <typeparamref name="T"/> по цепочке InnerException: сценарии
    /// нередко оборачивают ошибки автоматизации во внешние исключения, а типы новых сбоев
    /// должны распознаваться так же надёжно, как CDP-таймауты и Local API-таймауты.
    /// </summary>
    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T typed)
            {
                return typed;
            }
        }

        return null;
    }

    /// <summary>
    /// Backoff по счётчику подряд идущих переходных сбоев аккаунта:
    /// 1-й — базовая пауза, 2-й — 2 мин, 3-й — 5 мин, далее 10 мин (потолок).
    /// Базовая пауза (сетевые 5 мин, капча-кулдаун 15 мин) никогда не укорачивается кривой.
    /// </summary>
    public static TimeSpan Escalate(TimeSpan baseDelay, int consecutiveTransientFailures)
    {
        if (baseDelay <= TimeSpan.Zero)
        {
            return baseDelay;
        }

        var ceiling = Math.Max(baseDelay.TotalMinutes, MaxDelay.TotalMinutes);
        var minutes = consecutiveTransientFailures switch
        {
            <= 1 => (double)baseDelay.TotalMinutes,
            2 => Math.Max(baseDelay.TotalMinutes, 2),
            3 => Math.Max(baseDelay.TotalMinutes, 5),
            _ => Math.Max(baseDelay.TotalMinutes, MaxDelay.TotalMinutes)
        };

        return TimeSpan.FromMinutes(Math.Min(minutes, ceiling));
    }

    public static bool IsLocalChromeProfileBusy(Exception exception) =>
        LocalChromeLaunchDiagnostics.IsProfileBusy(exception);

    public static bool IsLocalChromeProfileBusy(string? text) =>
        LocalChromeLaunchDiagnostics.IsProfileBusy(text);

    public static string EventType(Exception exception) =>
        FromException(exception) is null ? "Error" : "Warning";
}

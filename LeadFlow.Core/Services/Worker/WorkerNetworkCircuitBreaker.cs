using LeadFlow.Core.Logging.Audit;

namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Глобальный сетевой предохранитель воркера: когда «сетевые» сбои сыплются по РАЗНЫМ
/// аккаунтам за короткое окно, проблема не в аккаунтах, а в канале/сети машины —
/// новые проходы заведомо упадут. Один раз приостанавливаем запуски всех проходов
/// вместо шторма одинаковых Warning-событий и бессмысленных открытий браузера.
/// </summary>
internal static class WorkerNetworkCircuitBreaker
{
    /// <summary>Скользящее окно учёта сбоев.</summary>
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

    /// <summary>Сколько держится «открытое» состояние (пауза запусков).</summary>
    public static readonly TimeSpan OpenDuration = TimeSpan.FromMinutes(5);

    /// <summary>Сколько разных аккаунтов с сетевым сбоем за окно считается штормом.</summary>
    public const int DistinctAccountsThreshold = 3;

    private static readonly object Gate = new();
    private static readonly Queue<(DateTime AtUtc, Guid AccountId)> Failures = new();
    private static DateTime? _openUntilUtc;
    private static DateTime _lastOpenLogUtc = DateTime.MinValue;

    /// <summary>Открыт ли предохранитель (запускать новые проходы нельзя).</summary>
    public static bool IsOpen(DateTime utcNow)
    {
        lock (Gate)
        {
            return _openUntilUtc is { } until && utcNow < until;
        }
    }

    /// <summary>До какого момента проходы приостановлены; null — предохранитель закрыт.</summary>
    public static DateTime? OpenUntilUtc() => OpenUntilUtc(DateTime.UtcNow);

    /// <summary>Вариант с инъекцией времени для тестов.</summary>
    internal static DateTime? OpenUntilUtc(DateTime utcNow)
    {
        lock (Gate)
        {
            return _openUntilUtc is { } until && until > utcNow ? until : null;
        }
    }

    /// <summary>
    /// Регистрирует сетевой сбой прохода аккаунта; при шторме (≥<see cref="DistinctAccountsThreshold"/>
    /// разных аккаунтов за <see cref="FailureWindow"/>) открывает предохранитель.
    /// </summary>
    public static void RegisterNetworkFailure(Guid accountId) =>
        RegisterNetworkFailure(accountId, DateTime.UtcNow);

    /// <summary>Вариант с инъекцией времени для тестов.</summary>
    internal static void RegisterNetworkFailure(Guid accountId, DateTime utcNow)
    {
        DateTime? openedUntil = null;
        lock (Gate)
        {
            while (Failures.Count > 0 && utcNow - Failures.Peek().AtUtc > FailureWindow)
            {
                Failures.Dequeue();
            }

            Failures.Enqueue((utcNow, accountId));
            var distinctAccounts = Failures.Select(static f => f.AccountId).Distinct().Count();
            if (distinctAccounts >= DistinctAccountsThreshold
                && (_openUntilUtc is null || utcNow >= _openUntilUtc))
            {
                _openUntilUtc = utcNow.Add(OpenDuration);
                openedUntil = _openUntilUtc;
                Failures.Clear();
            }
        }

        if (openedUntil is { } logUntil
            && DateTime.UtcNow - _lastOpenLogUtc >= OpenDuration)
        {
            _lastOpenLogUtc = DateTime.UtcNow;
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker: сетевой шторм — {DistinctAccountsThreshold}+ аккаунтов упали с сетевыми сбоями за " +
                $"{FailureWindow.TotalMinutes:0} мин. Запуск новых проходов приостановлен до {logUntil:HH:mm:ss} UTC.",
                DeskLinkAuditLogLevel.Warning,
                errorKey: "worker.network_breaker");
        }
    }

    /// <summary>Сброс состояния (только для тестов).</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Failures.Clear();
            _openUntilUtc = null;
        }
    }
}

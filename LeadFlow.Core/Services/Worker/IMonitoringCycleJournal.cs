namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Типизированный журнал проходов мониторинга (цикл аккаунта / субпрофили).
/// В LeadFlow desktop — no-op; в Orbita.Worker — отправка в API.
/// </summary>
public interface IMonitoringCycleJournal
{
    /// <summary>Начать проход аккаунта (новый cycle run).</summary>
    Guid BeginCycle(Guid accountId, string accountName);

    /// <summary>Начать проход субпрофиля (position — 1-based).</summary>
    Guid BeginSubProfile(
        Guid cycleId,
        string subProfileId,
        string subProfileName,
        int position,
        int total);

    /// <summary>Успешное завершение прохода субпрофиля (в т.ч. с 0 новых откликов).</summary>
    void CompleteSubProfile(
        Guid cycleId,
        Guid subProfileRunId,
        int foundCount,
        int publishedCount,
        int deferredCount = 0,
        int skippedDuplicateCount = 0,
        int collectedCount = 0,
        int captchaCount = 0,
        int captchaSolvedCount = 0,
        bool loginAttempted = false,
        bool loginSucceeded = false,
        int watchRefreshedCount = 0,
        int phoneChangedCount = 0,
        // Карточки без раскрытого телефона — отложены на следующий проход.
        int skippedNoPhoneCount = 0);

    /// <summary>Субпрофиль не запускался в этом цикле — очередь не дошла.</summary>
    void SkipSubProfile(
        Guid cycleId,
        string subProfileId,
        string subProfileName,
        int position,
        int total,
        string? errorType,
        string? errorMessage);

    /// <summary>Ошибка прохода субпрофиля.</summary>
    void FailSubProfile(
        Guid cycleId,
        Guid subProfileRunId,
        string? errorType,
        string? errorMessage,
        int foundCount = 0,
        int publishedCount = 0,
        int collectedCount = 0,
        int captchaCount = 0,
        int captchaSolvedCount = 0,
        bool loginAttempted = false,
        bool loginSucceeded = false);

    /// <summary>Цикл завершён нормально (все субпрофили в очереди отработаны).</summary>
    void CompleteCycle(Guid cycleId);

    /// <summary>Цикл прерван (блокирующая ошибка, капча, остановка).</summary>
    void AbortCycle(Guid cycleId, string? errorType = null, string? errorMessage = null);

    /// <summary>Цикл завершился фатальной ошибкой аккаунта.</summary>
    void FailCycle(Guid cycleId, string? errorType = null, string? errorMessage = null);

    /// <summary>Прервать все ещё открытые циклы (например, при остановке воркера).</summary>
    void AbortOpenCycles(string? errorType = null, string? errorMessage = null);

    Task FlushAsync(CancellationToken cancellationToken = default);
}

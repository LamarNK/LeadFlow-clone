namespace Orbita.Contracts;

/// <summary>Итоговый статус одного прохода аккаунта (цикл субпрофилей).</summary>
public static class MonitoringCycleRunStatuses
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Aborted = "Aborted";
    public const string Failed = "Failed";
}

/// <summary>Исход прохода одного субпрофиля внутри цикла.</summary>
public static class MonitoringSubProfileRunOutcomes
{
    public const string Started = "Started";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

/// <summary>Пакет журнала мониторинг-циклов от воркера (идемпотентный upsert по Id).</summary>
public sealed record MonitoringRunBatchRequest(
    Guid WorkerId,
    IReadOnlyList<MonitoringCycleRunUploadDto> Cycles);

public sealed record MonitoringCycleRunUploadDto(
    Guid Id,
    Guid AccountId,
    string AccountName,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    string Status,
    IReadOnlyList<MonitoringSubProfileRunUploadDto> SubProfiles);

public sealed record MonitoringSubProfileRunUploadDto(
    Guid Id,
    string SubProfileId,
    string SubProfileName,
    int Position,
    int Total,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    string Outcome,
    string? ErrorType,
    string? ErrorMessage,
    int FoundCount = 0,
    int PublishedCount = 0,
    int DeferredCount = 0,
    int SkippedDuplicateCount = 0,
    int CollectedCount = 0,
    int CaptchaCount = 0,
    int CaptchaSolvedCount = 0,
    bool LoginAttempted = false,
    bool LoginSucceeded = false,
    int WatchRefreshedCount = 0,
    int PhoneChangedCount = 0,
    // Карточки без раскрытого телефона — отложены на следующий проход (не публикуются, не дубли).
    int SkippedNoPhoneCount = 0);

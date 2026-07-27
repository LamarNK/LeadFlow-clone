namespace Orbita.Contracts;

public sealed record CreateWorkerRequest(string DisplayName, Guid? OfficeId = null);

public sealed record CreateWorkerResponse(Guid WorkerId, string ApiKey, string DisplayName);

public sealed record WorkerSystemMetricsDto(
    double CpuPercent,
    double RamPercent,
    long RamUsedMb,
    long RamTotalMb);

public sealed record WorkerAccountConfigDto(
    Guid AccountId,
    string AdsPowerProfileId,
    string DisplayName,
    bool IsEnabled,
    string? AdsPowerApiBaseUrl,
    string? AdsPowerApiKey,
    DateTime? SubProfilesRefreshRequestedAtUtc = null,
    IReadOnlyList<string>? DisabledSubProfileIds = null,
    string? Status = null,
    string? LastErrorMessage = null,
    DateTime? LastMonitoringAtUtc = null,
    DateTime? LastAuthCheckAtUtc = null,
    string? SubProfilesJson = null,
    DateTime? SubProfilesRefreshedAtUtc = null,
    int ActiveAdsCount = 0,
    int BlockedCount = 0,
    int DraftsCount = 0,
    /// <summary>Логин/телефон Avito для автологина воркера (только worker config, не в телеметрии панели).</summary>
    string? AvitoLogin = null,
    /// <summary>Пароль Avito (plaintext только в защищённом worker config channel).</summary>
    string? AvitoPassword = null);

public sealed record UpdateWorkerSubProfileRequest(bool IsEnabledInPanel);

public static class WorkerCommands
{
    public const string Restart = "restart";
    public const string Pause = "pause";
}

public sealed record WorkerCommandRequest(string Command);

public sealed record WorkerUpdateOfferDto(
    string Version,
    string DownloadPath,
    string Sha256,
    long FileSize,
    string? ReleaseNotes);

public sealed record WorkerConfigDto(
    Guid WorkerId,
    int MaxConcurrentAccounts,
    string? AdsPowerApiBaseUrl,
    string? AdsPowerApiKey,
    IReadOnlyList<WorkerAccountConfigDto> Accounts,
    string? PendingCommand = null,
    WorkerUpdateOfferDto? UpdateOffer = null,
    WorkerPendingCaptchaSessionDto? PendingCaptchaSession = null,
    WorkerPendingBrowserMonitorSessionDto? PendingBrowserMonitorSession = null,
    bool ResponseFilterEnabled = false,
    bool ResponseFilterExcludeFemale = false,
    int? ResponseFilterMaxAge = null,
    bool ResponseFilterExcludeMale = false,
    int? ResponseFilterMaxAgeMale = null,
    int? ResponseFilterMaxAgeFemale = null,
    /// <summary>Пропускать отклики старше N дней (по дате отклика из чата Avito). null — без ограничения.</summary>
    int? ResponseFilterMaxResponseAgeDays = null,
    bool ResponseHighlightEnabled = false,
    string? ResponseHighlightAgeBuckets = null,
    bool AutoScheduleEnabled = false,
    string? AutoScheduleDays = null,
    string? AutoScheduleFromLocalTime = null,
    string? AutoScheduleToLocalTime = null,
    bool MessengerAutoReplyEnabled = false,
    string? MessengerAutoReplyMessage = null,
    /// <summary>
    /// Через сколько часов без смены номера отправлять метрику «не менялся».
    /// null — default 24; 0 — не отправлять метрику стабильности (смену номера всё равно трекаем).
    /// </summary>
    int? PhoneUnchangedHours = null)
{
    public ResponseCollectionFilters ResponseFilters =>
        ResponseCollectionFilters.NormalizeLegacy(
            ResponseFilterEnabled,
            ResponseFilterExcludeFemale,
            ResponseFilterMaxAge,
            ResponseFilterExcludeMale,
            ResponseFilterMaxAgeMale,
            ResponseFilterMaxAgeFemale,
            ResponseFilterMaxResponseAgeDays);

    public int EffectivePhoneUnchangedHours =>
        ResponsePhoneWatchRules.ResolveUnchangedHours(PhoneUnchangedHours);
}

public sealed record WorkerAccountSyncItemDto(
    string AdsPowerProfileId,
    string DisplayName);

public sealed record WorkerAccountSyncRequest(
    IReadOnlyList<WorkerAccountSyncItemDto> Accounts);

public sealed record WorkerCandidateDto(
    Guid AccountId,
    string AccountName,
    string Source,
    string SourceResponseId,
    string CardFingerprint,
    string FullName,
    int? Age,
    string? Gender,
    string PhoneRaw,
    string City,
    string Vacancy,
    string VacancyUrl,
    string MessengerUrl,
    string AvitoSubProfileId,
    string RawText,
    string ChatMessagesJson,
    DateTime CreatedAt,
    string AvitoSubProfileName = "",
    DateTime CollectedAt = default,
    /// <summary>Метрика номера: <see cref="ResponsePhoneMetricKinds"/>.</summary>
    string PhoneMetricKind = "",
    string? PreviousPhoneRaw = null,
    string? PreviousPhoneNormalized = null,
    int? PhoneUnchangedHours = null,
    DateTime? PhoneChangedAtUtc = null);

public sealed record WorkerCandidateBatchRequest(
    IReadOnlyList<WorkerCandidateDto> Candidates);

public sealed record CandidateLookupProfileDto(
    string FullName,
    int? Age,
    string City,
    string PhoneNormalized,
    /// <summary>Дата отклика (чат); для неполного имени — окно ~неделя на API.</summary>
    DateTime? ResponseAtUtc = null);

public sealed record WorkerCandidateLookupRequest(
    Guid AccountId,
    string DuplicateScope,
    IReadOnlyList<string> SourceResponseIds,
    IReadOnlyList<string> PhoneNormalized,
    bool IncludeAllKnownPhones = false,
    string? AvitoSubProfileId = null,
    IReadOnlyList<string>? CardFingerprints = null,
    IReadOnlyList<CandidateLookupProfileDto>? Profiles = null);

public sealed record WorkerCandidateLookupResponse(
    IReadOnlyList<string> ExistingSourceResponseIds,
    IReadOnlyList<string> ExistingPhones,
    IReadOnlyList<string> ExistingCardFingerprints,
    IReadOnlyList<int> MatchedProfileIndexes);

public sealed record WorkerMonitoringStatsDto(
    double HistoricalHeatScore,
    DashboardStatsDto Stats);

public sealed record WorkerCandidateIngestionItemResultDto(
    Guid? Id,
    string SourceResponseId,
    string Status,
    string? ErrorMessage);

public sealed record WorkerCandidateIngestionResultDto(
    int Received,
    int Ingested,
    int SkippedDuplicates,
    int Errors,
    IReadOnlyList<WorkerCandidateIngestionItemResultDto> Items);

public sealed record UpdateWorkerSettingsRequest(
    int MaxConcurrentAccounts,
    string? AdsPowerApiBaseUrl = null,
    string? AdsPowerApiKey = null,
    bool ResponseFilterEnabled = false,
    bool ResponseFilterExcludeFemale = false,
    int? ResponseFilterMaxAge = null,
    bool ResponseFilterExcludeMale = false,
    int? ResponseFilterMaxAgeMale = null,
    int? ResponseFilterMaxAgeFemale = null,
    /// <summary>Пропускать отклики старше N дней (по дате отклика из чата Avito). null — без ограничения.</summary>
    int? ResponseFilterMaxResponseAgeDays = null,
    bool ResponseHighlightEnabled = false,
    string? ResponseHighlightAgeBuckets = null,
    bool AutoScheduleEnabled = false,
    string? AutoScheduleDays = null,
    string? AutoScheduleFromLocalTime = null,
    string? AutoScheduleToLocalTime = null,
    bool MessengerAutoReplyEnabled = false,
    string? MessengerAutoReplyMessage = null,
    /// <summary>Часов без смены номера до метрики «не менялся». null=24, 0=выкл стабильность.</summary>
    int? PhoneUnchangedHours = null);

public sealed record UpdateWorkerAccountRequest(bool IsEnabledInPanel);

/// <summary>
/// Обновление логина/пароля Avito для аккаунта.
/// <paramref name="Password"/> = null/пусто — не менять сохранённый пароль (если <paramref name="Clear"/> = false).
/// </summary>
public sealed record UpdateWorkerAccountCredentialsRequest(
    string? Login,
    string? Password = null,
    bool Clear = false);

public sealed record WorkerAccountCredentialsDto(
    Guid AccountId,
    string? Login,
    bool HasPassword);

public sealed record BulkWorkersMonitoringResultDto(
    int UpdatedCount,
    int UnchangedCount,
    int TotalCount);

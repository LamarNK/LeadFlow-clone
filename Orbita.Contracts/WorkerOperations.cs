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
    int DraftsCount = 0);

public sealed record UpdateWorkerSubProfileRequest(bool IsEnabledInPanel);

public static class WorkerCommands
{
    public const string Restart = "restart";
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
    WorkerPendingBrowserMonitorSessionDto? PendingBrowserMonitorSession = null);

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
    string AvitoSubProfileName = "");

public sealed record WorkerCandidateBatchRequest(
    IReadOnlyList<WorkerCandidateDto> Candidates);

public sealed record WorkerCandidateLookupRequest(
    Guid AccountId,
    string DuplicateScope,
    IReadOnlyList<string> SourceResponseIds,
    IReadOnlyList<string> PhoneNormalized,
    bool IncludeAllKnownPhones = false,
    string? AvitoSubProfileId = null,
    IReadOnlyList<string>? CardFingerprints = null);

public sealed record WorkerCandidateLookupResponse(
    IReadOnlyList<string> ExistingSourceResponseIds,
    IReadOnlyList<string> ExistingPhones,
    IReadOnlyList<string> ExistingCardFingerprints);

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
    string? AdsPowerApiKey = null);

public sealed record UpdateWorkerAccountRequest(bool IsEnabledInPanel);
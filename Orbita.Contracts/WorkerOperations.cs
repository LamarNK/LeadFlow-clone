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
    string? AdsPowerApiKey);

public sealed record WorkerConfigDto(
    Guid WorkerId,
    int MaxConcurrentAccounts,
    string? AdsPowerApiBaseUrl,
    string? AdsPowerApiKey,
    IReadOnlyList<WorkerAccountConfigDto> Accounts);

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
    string FullName,
    int? Age,
    string PhoneRaw,
    string City,
    string Vacancy,
    string VacancyUrl,
    string MessengerUrl,
    string AvitoSubProfileId,
    string RawText,
    DateTime CreatedAt);

public sealed record WorkerCandidateBatchRequest(
    IReadOnlyList<WorkerCandidateDto> Candidates);

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
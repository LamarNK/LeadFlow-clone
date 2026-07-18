namespace Orbita.Contracts;

public sealed record ResponseBitrixDeliveryDto(
    Guid Id,
    Guid BitrixInstanceId,
    string BitrixLabel,
    string Outcome,
    string? BitrixEntityId,
    string? BitrixEntityType,
    string? BitrixEntityUrl,
    string? ErrorMessage,
    string Source,
    DateTime CreatedAtUtc);

public sealed record ResponseListItemDto(
    Guid Id,
    Guid OfficeId,
    Guid WorkerId,
    string WorkerName,
    Guid AccountId,
    string AccountName,
    string Source,
    string SourceResponseId,
    string FullName,
    int? Age,
    string? Gender,
    string PhoneRaw,
    string PhoneNormalized,
    string Vacancy,
    string VacancyUrl,
    string MessengerUrl,
    string City,
    string Status,
    bool IsLocalDuplicate,
    bool IsBitrixDuplicate,
    string? BitrixEntityId,
    string? BitrixEntityType,
    string? BitrixEntityUrl,
    Guid? BitrixInstanceId,
    string? BitrixInstanceName,
    string? BitrixInstanceSignature,
    Guid? DuplicateBitrixInstanceId,
    string? DuplicateBitrixInstanceName,
    string AvitoSubProfileId,
    string? AvitoSubProfileName,
    DateTime CreatedAt,
    DateTime? ProcessedAt,
    IReadOnlyList<ResponseBitrixDeliveryDto> BitrixDeliveries);

public sealed record ResponseDetailDto(
    Guid Id,
    Guid OfficeId,
    Guid WorkerId,
    string WorkerName,
    Guid AccountId,
    string AccountName,
    string Source,
    string SourceResponseId,
    string FullName,
    string FirstName,
    string LastName,
    string MiddleName,
    int? Age,
    string? Gender,
    string PhoneRaw,
    string PhoneNormalized,
    string City,
    string Vacancy,
    string VacancyUrl,
    string MessengerUrl,
    string AvitoSubProfileId,
    string? AvitoSubProfileName,
    string RawText,
    string ChatMessagesJson,
    string Status,
    bool IsLocalDuplicate,
    bool IsBitrixDuplicate,
    string? DuplicateSummary,
    string? BitrixEntityId,
    string? BitrixEntityType,
    string? BitrixEntityUrl,
    string? BitrixContactId,
    Guid? BitrixInstanceId,
    string? BitrixInstanceName,
    string? BitrixInstanceSignature,
    Guid? DuplicateBitrixInstanceId,
    string? DuplicateBitrixInstanceName,
    string? DistributionMode,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? ProcessedAt,
    IReadOnlyList<ResponseBitrixDeliveryDto> BitrixDeliveries);

public sealed record ResponsesPageDto(
    IReadOnlyList<ResponseListItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record ResponsesSummaryDto(
    int Total,
    int Unique,
    int Duplicates,
    int Sent,
    int UniqueAuthors,
    double? AvgResponseMinutes);

public sealed record ResponseFilterAccountDto(
    Guid AccountId,
    string AccountName);

public sealed record ResponseFilterVacancyDto(
    string Vacancy,
    int Count);

public sealed record OfficeBitrixWebhookDto(
    string UserId,
    string Email,
    string? PortalHost,
    string ValidationStatus,
    bool IsPrimaryForIngestion);

public sealed record ResendBitrixResultDto(
    bool Success,
    string Status,
    string? BitrixEntityId,
    string? ErrorMessage);

public sealed record SendBitrixResultDto(
    bool Success,
    string Status,
    string? BitrixEntityId,
    Guid? BitrixInstanceId,
    string? BitrixInstanceName,
    string? ErrorMessage);

public sealed record BulkSendResponsesToBitrixRequest(
    IReadOnlyList<Guid> ResponseIds,
    Guid BitrixInstanceId);

public sealed record BulkSendBitrixItemResultDto(
    Guid ResponseId,
    bool Success,
    string Status,
    string? ErrorMessage);

public sealed record BulkSendBitrixResultDto(
    int Total,
    int Succeeded,
    int Failed,
    IReadOnlyList<BulkSendBitrixItemResultDto> Items);
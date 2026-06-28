namespace Orbita.Contracts;

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
    DateTime CreatedAt,
    DateTime? ProcessedAt);

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
    string PhoneRaw,
    string PhoneNormalized,
    string City,
    string Vacancy,
    string VacancyUrl,
    string MessengerUrl,
    string AvitoSubProfileId,
    string RawText,
    string Status,
    bool IsLocalDuplicate,
    bool IsBitrixDuplicate,
    string? DuplicateSummary,
    string? BitrixEntityId,
    string? BitrixContactId,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? ProcessedAt);

public sealed record ResponsesPageDto(
    IReadOnlyList<ResponseListItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record ResponsesSummaryDto(
    int Total,
    int Unique,
    int Duplicates,
    int UniqueAuthors,
    double? AvgResponseMinutes);

public sealed record ResponseFilterAccountDto(
    Guid AccountId,
    string AccountName);

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
namespace Orbita.Contracts;

public static class AvitoAdListingStates
{
    public const string Active = "Active";
    public const string ApproachingExpiry = "ApproachingExpiry";
    public const string ExpiresToday = "ExpiresToday";
    public const string Expired = "Expired";
    public const string NotActive = "NotActive";
    public const string UnknownPublicationDate = "UnknownPublicationDate";
    public const string ParseFailed = "ParseFailed";
}

public static class AvitoAdPublicationDateSources
{
    public const string Exact = "Exact";
    public const string Unknown = "Unknown";
    public const string Estimated = "Estimated";
    public const string ListExpiry = "ListExpiry";
}

public sealed record WorkerAvitoAdDto(
    Guid Id,
    Guid WorkerId,
    Guid AccountId,
    string AvitoSubProfileId,
    string AvitoItemId,
    string Title,
    string Url,
    string StatusText,
    DateTime? PublishedAtUtc,
    string PublicationDateSource,
    int? AgeDays,
    int? RemainingDays,
    DateTime? ExpiresAtUtc,
    DateTime? LastSeenAtUtc,
    DateTime? DetailCheckedAtUtc,
    DateTime? LastSuccessfulListCheckAtUtc,
    bool IsActive,
    string State,
    string? LastParseError,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record WorkerAvitoAdSyncItemDto(
    string AvitoItemId,
    string Title,
    string Url,
    string StatusText,
    DateTime? PublishedAtUtc,
    string PublicationDateSource,
    int? AgeDays,
    int? RemainingDays,
    DateTime? ExpiresAtUtc,
    DateTime? LastSeenAtUtc,
    DateTime? DetailCheckedAtUtc,
    bool IsActive,
    string State,
    string? LastParseError);

public sealed record WorkerAvitoAdSyncRequest(
    Guid WorkerId,
    Guid AccountId,
    string AvitoSubProfileId,
    bool ListComplete,
    DateTime CapturedAtUtc,
    IReadOnlyList<WorkerAvitoAdSyncItemDto> Items);

public sealed record WorkerAvitoAdSyncResponse(int Upserted, int Deactivated);

public sealed record AvitoAdListingListItem(
    Guid Id,
    Guid WorkerId,
    string WorkerName,
    Guid AccountId,
    string AccountName,
    string AvitoSubProfileId,
    string SubProfileName,
    string AvitoItemId,
    string Title,
    string Url,
    string StatusText,
    DateTime? PublishedAtUtc,
    DateTime? ExpiresAtUtc,
    int? AgeDays,
    int? RemainingDays,
    string State,
    string PublicationDateSource,
    DateTime? LastSeenAtUtc,
    DateTime? DetailCheckedAtUtc,
    bool IsActive,
    string? LastParseError);

public sealed record AvitoAdListingSummary(
    int ActiveCount,
    int UnknownDateCount,
    int ExpiringIn7DaysCount,
    int ExpiresTodayCount,
    int ExpiredCount);

public sealed record AvitoAdListingListResponse(
    IReadOnlyList<AvitoAdListingListItem> Items,
    AvitoAdListingSummary Summary,
    int Total);

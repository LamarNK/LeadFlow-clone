namespace LeadFlow.Core.Models;

public sealed class AvitoAdListingRecord
{
    public Guid Id { get; init; }
    public Guid WorkerId { get; init; }
    public Guid AccountId { get; init; }
    public string AvitoSubProfileId { get; init; } = string.Empty;
    public string AvitoItemId { get; init; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public DateTime? PublishedAtUtc { get; set; }
    public string PublicationDateSource { get; set; } = AvitoAdPublicationDateSources.Unknown;
    public int? AgeDays { get; set; }
    public int? RemainingDays { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime? DetailCheckedAtUtc { get; set; }
    public DateTime? LastSuccessfulListCheckAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public string State { get; set; } = AvitoAdListingStates.UnknownPublicationDate;
    public string? LastParseError { get; set; }
    public string SourceTab { get; set; } = AvitoAdStatus.ActiveTab;
    public string ErrorReason { get; set; } = string.Empty;
    public bool CanPublish { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public string Salary { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string AddressText { get; set; } = string.Empty;
    public string DistrictText { get; set; } = string.Empty;
    public int Views { get; set; }
    public int Contacts { get; set; }
    public int Favorites { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

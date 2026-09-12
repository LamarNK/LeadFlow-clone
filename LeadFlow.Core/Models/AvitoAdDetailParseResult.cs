namespace LeadFlow.Core.Models;

public sealed class AvitoAdDetailParseResult
{
    public bool Success { get; init; }
    public string? FailureReason { get; init; }
    public string AvitoItemId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public DateTime? PublishedAtUtc { get; init; }
    public string PublicationDateSource { get; init; } = AvitoAdPublicationDateSources.Unknown;
    public int? RemainingDays { get; init; }
    public string RawItemIdText { get; init; } = string.Empty;
    public string RawLifeBarText { get; init; } = string.Empty;
}

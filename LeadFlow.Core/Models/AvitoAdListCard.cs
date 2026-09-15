namespace LeadFlow.Core.Models;

/// <summary>Карточка объявления со вкладки «Активные».</summary>
public sealed class AvitoAdListCard
{
    public string AvitoItemId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Href { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? UrlParseError { get; init; }
    public int? AgeDays { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }
    public int? RemainingDays { get; init; }
    public string? ExpiryParseError { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string SourceTab { get; init; } = AvitoAdStatus.ActiveTab;
    public string ErrorReason { get; init; } = string.Empty;
    public bool CanPublish { get; init; }
}

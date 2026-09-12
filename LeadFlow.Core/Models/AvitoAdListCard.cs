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
    public string StatusText { get; init; } = string.Empty;
}

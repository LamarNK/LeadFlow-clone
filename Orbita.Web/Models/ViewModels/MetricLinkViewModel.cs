namespace Orbita.Web.Models.ViewModels;

public sealed class MetricLinkViewModel
{
    public int Value { get; init; }
    public string? Href { get; init; }
    public string Title { get; init; } = string.Empty;
}
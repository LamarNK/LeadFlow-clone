namespace Orbita.Web.Models.ViewModels;

public sealed class GlobalSearchResultViewModel
{
    public static GlobalSearchResultViewModel Empty { get; } = new();

    public string Query { get; init; } = string.Empty;
    public IReadOnlyList<SearchHitViewModel> Workers { get; init; } = [];
    public IReadOnlyList<SearchHitViewModel> Accounts { get; init; } = [];
    public IReadOnlyList<SearchHitViewModel> Responses { get; init; } = [];
    public IReadOnlyList<SearchHitViewModel> Errors { get; init; } = [];
}

public sealed class SearchHitViewModel
{
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string Url { get; init; } = "/";
    public string IconClass { get; init; } = "fa-solid fa-magnifying-glass";
}
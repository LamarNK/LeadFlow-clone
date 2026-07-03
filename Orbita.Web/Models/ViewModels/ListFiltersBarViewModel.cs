namespace Orbita.Web.Models.ViewModels;

public sealed class ListFiltersBarViewModel
{
    public string FormAction { get; init; } = string.Empty;
    public string FormClass { get; init; } = "orbita-filters-form";
    public string? SearchName { get; init; } = "q";
    public string SearchPlaceholder { get; init; } = "Поиск...";
    public string? SearchValue { get; init; }
    public string FilterPanelId { get; init; } = string.Empty;
    public string FilterToggleClass { get; init; } = "orbita-filters-toggle";
    public bool HasActiveFilters { get; init; }
    public int ActiveFilterCount { get; init; }
    public IReadOnlyList<ActiveFilterChipViewModel> ActiveFilterChips { get; init; } = [];
    public string ResetUrl { get; init; } = string.Empty;
    public bool ShowApply { get; init; }
    public string? FilterFieldsPartialName { get; init; }
    public object? FilterFieldsModel { get; init; }
    public string? ExtraFormContent { get; init; }
}
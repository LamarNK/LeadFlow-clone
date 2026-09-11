namespace Orbita.Web.Models.ViewModels;

public sealed class StatisticsMultiSelectViewModel
{
    public string FieldName { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string AllLabel { get; init; } = string.Empty;
    public IReadOnlyList<Guid> SelectedIds { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> Options { get; init; } = [];
}

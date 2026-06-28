namespace Orbita.Web.Models.ViewModels;

public sealed class TableEmptyStateViewModel
{
    public string IconClass { get; init; } = "fa-solid fa-inbox";
    public string Title { get; init; } = "Нет записей";
    public string? Description { get; init; }
    public string? CtaLabel { get; init; }
    public string? CtaUrl { get; init; }
}

public sealed class PaginationPartialViewModel
{
    public PaginationViewModel Pagination { get; init; } = new();
    public string Controller { get; init; } = string.Empty;
    public string Action { get; init; } = "Index";
    public object? RouteValues { get; init; }
    public string? ExtraClass { get; init; }
}
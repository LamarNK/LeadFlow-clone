namespace Orbita.Web.Models.ViewModels;

public sealed class TableSortState
{
    public string Column { get; init; } = string.Empty;

    public bool Descending { get; init; }

    public string Dir => Descending ? "desc" : "asc";

    public static TableSortState Create(string column, bool descending) =>
        new() { Column = column, Descending = descending };
}

public sealed class TableSortThViewModel
{
    public required string Label { get; init; }

    public required string Column { get; init; }

    public TableSortState Sort { get; init; } = new();

    public required string Controller { get; init; }

    public required string Action { get; init; }

    public object? RouteValues { get; init; }

    public bool Numeric { get; init; }
}
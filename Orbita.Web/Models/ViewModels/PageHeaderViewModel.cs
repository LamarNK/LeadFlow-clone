namespace Orbita.Web.Models.ViewModels;

public sealed class PageHeaderViewModel
{
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public bool ShowRefresh { get; init; }
    public bool ShowDateRange { get; init; }
    public string DateRangeLabel { get; init; } =
        $"{DateTime.Today:dd.MM.yyyy} — {DateTime.Today:dd.MM.yyyy}";

    public string UserInitial { get; init; } = "А";

    public string UserDisplayName { get; init; } = "Администратор";

    public string UserEmail { get; init; } = "admin@orbita.local";
}
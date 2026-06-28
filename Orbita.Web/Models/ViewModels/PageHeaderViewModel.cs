namespace Orbita.Web.Models.ViewModels;

public sealed class PageHeaderViewModel
{
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public bool ShowRefresh { get; init; }
    public bool ShowDateRange { get; init; }
    public string DateRangeLabel { get; init; } =
        DateTime.Today.ToString("dd.MM.yyyy");
    public DateTime DateFrom { get; init; } = DateTime.Today;
    public DateTime DateTo { get; init; } = DateTime.Today;
    public string? ActivePeriodPreset { get; init; }

    public string UserInitial { get; init; } = "А";

    public string UserDisplayName { get; init; } = "Администратор";

    public string UserEmail { get; init; } = "admin@orbita.local";
}
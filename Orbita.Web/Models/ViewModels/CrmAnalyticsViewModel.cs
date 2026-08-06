using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class CrmAnalyticsViewModel
{
    public PageHeaderViewModel Header { get; init; } = new()
    {
        Title = "Аналитика CRM"
    };

    public CrmAnalyticsDto? Analytics { get; init; }

    public bool IsAdmin { get; init; }

    public string CurrentUserId { get; init; } = string.Empty;

    public string? SelectedManagerUserId { get; init; }

    public string OfficeContextLabel { get; init; } = "Текущий офис";

    public string? ErrorMessage { get; init; }
}

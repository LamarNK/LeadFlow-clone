using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class CrmMissedCallsViewModel
{
    public PageHeaderViewModel Header { get; init; } = new();
    public CrmMissedCallsDto? Data { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string? ManagerUserId { get; init; }
    public string? Status { get; init; }
    public Guid? CallId { get; init; }
    public bool ShowManagers { get; init; }
    public int TimeZoneOffset { get; init; }
}

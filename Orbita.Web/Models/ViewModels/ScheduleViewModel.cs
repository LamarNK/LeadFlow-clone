using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class ScheduleViewModel
{
    public PageHeaderViewModel Header { get; init; } = new();
    public WorkerScheduleOfficeDto? Schedule { get; init; }
    public WorkerScheduleGroupDto? SelectedGroup { get; init; }
    public bool NeedsOfficeSelection { get; init; }
    public string? Error { get; init; }
}

using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed record CrmTasksViewModel(
    IReadOnlyList<CrmTaskDto> Tasks,
    IReadOnlyList<CrmManagerDto> Managers,
    string SelectedScope,
    string CurrentUserId,
    bool IsAdmin,
    string? ManagerUserId,
    bool CanFilterResponsible,
    int BrowserUtcOffsetMinutes = 0);

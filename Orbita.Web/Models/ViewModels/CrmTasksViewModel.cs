using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed record CrmTasksViewModel(
    IReadOnlyList<CrmTaskDto> Tasks,
    IReadOnlyList<CrmManagerDto> Managers,
    int OpenTaskCount,
    int OverdueTaskCount);

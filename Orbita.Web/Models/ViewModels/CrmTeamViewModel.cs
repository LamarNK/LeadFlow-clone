using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed record CrmTeamViewModel(
    CrmBoardDto Board,
    IReadOnlyList<CrmTaskDto> Tasks,
    string SelectedTaskScope,
    bool CanManageStaff = false,
    IReadOnlyList<PanelUserDto>? Staff = null);

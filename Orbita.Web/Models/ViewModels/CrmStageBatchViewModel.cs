using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed record CrmTileViewModel(
    CrmCandidateCardDto Card,
    IReadOnlyList<CrmManagerDto> Managers,
    string StageName,
    string? CurrentUserId,
    string ReturnUrl,
    bool CanEdit,
    bool IsAdmin,
    bool CanUseBulkActions);

public sealed record CrmStageBatchViewModel(
    IReadOnlyList<CrmCandidateCardDto> Cards,
    IReadOnlyList<CrmManagerDto> Managers,
    string StageName,
    string? CurrentUserId,
    string ReturnUrl,
    bool CanEdit,
    bool IsAdmin,
    bool CanUseBulkActions);

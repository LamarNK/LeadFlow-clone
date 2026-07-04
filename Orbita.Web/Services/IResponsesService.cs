using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IResponsesService
{
    Task<ResponsesIndexViewModel> GetIndexAsync(
        string? from,
        string? to,
        string? status,
        Guid? workerId,
        Guid? accountId,
        string? vacancy,
        string? search,
        Guid? selectedId,
        int page = 1,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> ResendToBitrixAsync(Guid id, CancellationToken ct = default);

    Task<ResponseDetailJsonViewModel?> GetDetailJsonAsync(Guid id, CancellationToken ct = default);
}
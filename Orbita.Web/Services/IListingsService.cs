using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IListingsService
{
    Task<ListingsIndexViewModel> GetIndexAsync(
        string? searchQuery = null,
        string? tab = null,
        IReadOnlyList<Guid>? workerIds = null,
        IReadOnlyList<Guid>? accountIds = null,
        IReadOnlyList<string>? subProfileIds = null,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default);
}

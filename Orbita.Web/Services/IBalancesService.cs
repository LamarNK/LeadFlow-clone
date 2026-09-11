using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IBalancesService
{
    Task<BalancesIndexViewModel> GetIndexAsync(
        string? tab = null,
        string? query = null,
        bool history = false,
        int page = 1,
        IReadOnlyList<Guid>? excludedWorkerIds = null,
        CancellationToken ct = default);
}

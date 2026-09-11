using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IBalancesService
{
    Task<BalancesIndexViewModel> GetIndexAsync(
        string? tab = null,
        string? query = null,
        bool history = false,
        int page = 1,
        IReadOnlyList<Guid>? workerIds = null,
        CancellationToken ct = default);
}

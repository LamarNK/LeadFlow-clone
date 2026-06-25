using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IWorkersService
{
    Task<WorkersIndexViewModel> GetIndexAsync(string? searchQuery = null, int page = 1, CancellationToken ct = default);
    Task<WorkerDetailsViewModel?> GetDetailsAsync(Guid id, CancellationToken ct = default);
}
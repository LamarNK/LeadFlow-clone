using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IWorkersService
{
    Task<WorkersIndexViewModel> GetIndexAsync(CancellationToken ct = default);
    Task<WorkerDetailsViewModel?> GetDetailsAsync(Guid id, CancellationToken ct = default);
}
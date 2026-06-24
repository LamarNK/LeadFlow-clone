using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IDashboardService
{
    Task<DashboardViewModel> GetDashboardAsync(CancellationToken ct = default);
}
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IDashboardService
{
    Task<DashboardViewModel> GetDashboardAsync(DashboardPeriod period, CancellationToken ct = default);
}
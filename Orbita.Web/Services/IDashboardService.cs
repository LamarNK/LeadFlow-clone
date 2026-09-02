using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IDashboardService
{
    Task<DashboardViewModel> GetDashboardAsync(
        DashboardPeriod period,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default);
}

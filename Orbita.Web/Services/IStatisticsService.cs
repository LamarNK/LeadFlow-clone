using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IStatisticsService
{
    Task<StatisticsViewModel> GetIndexAsync(DashboardPeriod period, CancellationToken ct = default);
}
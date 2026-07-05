using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IStatisticsService
{
    Task<StatisticsViewModel> GetIndexAsync(
        DashboardPeriod period,
        IReadOnlyList<Guid>? workerIds = null,
        IReadOnlyList<Guid>? accountIds = null,
        CancellationToken ct = default);
}
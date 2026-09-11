using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IStatisticsService
{
    Task<StatisticsViewModel> GetIndexAsync(
        DashboardPeriod period,
        IReadOnlyList<Guid>? workerIds = null,
        IReadOnlyList<Guid>? accountIds = null,
        string? vacancy = null,
        CancellationToken ct = default,
        bool includeFilterCatalog = true);
}
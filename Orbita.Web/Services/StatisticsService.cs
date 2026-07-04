using Microsoft.Extensions.Options;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class StatisticsService(
    OrbitaApiClient api,
    IOfficeContext officeContext,
    IOptions<DesignPreviewOptions> previewOptions) : IStatisticsService
{
    public async Task<StatisticsViewModel> GetIndexAsync(DashboardPeriod period, CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildStatisticsIndexViewModel(period, officeContext);
        }

        var data = await api.GetStatisticsAsync(period.From, period.To, ct);
        if (data is null)
        {
            return new StatisticsViewModel
            {
                Header = PageHeaderBuilder.WithOfficeScope(PageHeaderBuilder.Statistics(period), officeContext),
                ErrorMessage = "Не удалось загрузить статистику. Выйдите из панели и войдите снова."
            };
        }

        return StatisticsIndexBuilder.Build(data, period, officeContext);
    }
}
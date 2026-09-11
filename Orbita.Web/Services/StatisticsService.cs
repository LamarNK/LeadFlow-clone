using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class StatisticsService(
    OrbitaApiClient api,
    IOfficeContext officeContext,
    IOptions<DesignPreviewOptions> previewOptions) : IStatisticsService
{
    public async Task<StatisticsViewModel> GetIndexAsync(
        DashboardPeriod period,
        IReadOnlyList<Guid>? workerIds = null,
        IReadOnlyList<Guid>? accountIds = null,
        string? vacancy = null,
        CancellationToken ct = default,
        bool includeFilterCatalog = true)
    {
        var filters = new StatisticsFiltersViewModel
        {
            WorkerIds = NormalizeIds(workerIds),
            AccountIds = NormalizeIds(accountIds),
            VacancyQuery = string.IsNullOrWhiteSpace(vacancy) ? null : vacancy.Trim()
        };

        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildStatisticsIndexViewModel(period, officeContext, filters);
        }

        var workersTask = includeFilterCatalog
            ? api.GetWorkersAsync(ct)
            : Task.FromResult<IReadOnlyList<WorkerListItem>?>([]);
        var accountsTask = includeFilterCatalog
            ? api.GetResponseFilterAccountsAsync(ct)
            : Task.FromResult<IReadOnlyList<ResponseFilterAccountDto>?>([]);
        var dataTask = api.GetStatisticsAsync(
            period.From,
            period.To,
            filters.WorkerIds,
            filters.AccountIds,
            filters.VacancyQuery,
            period.TimeZoneOffsetMinutes,
            ct);
        await Task.WhenAll(workersTask, accountsTask, dataTask);

        var workers = await workersTask ?? [];
        var accounts = await accountsTask ?? [];
        var workerOptions = BuildWorkerOptions(workers);
        var accountOptions = BuildAccountOptions(accounts);
        var activeFilterChips = FilterChipsBuilder.ForStatistics(filters, period, workerOptions, accountOptions);

        var data = await dataTask;
        if (data is null)
        {
            return new StatisticsViewModel
            {
                Header = PageHeaderBuilder.WithOfficeScope(PageHeaderBuilder.Statistics(period), officeContext),
                Filters = filters,
                WorkerOptions = workerOptions,
                AccountOptions = accountOptions,
                HasActiveFilters = StatisticsIndexBuilder.HasActiveFilters(filters),
                ActiveFilterChips = activeFilterChips,
                ErrorMessage = "Не удалось загрузить статистику. Выйдите из панели и войдите снова."
            };
        }

        return StatisticsIndexBuilder.Build(
            data,
            period,
            officeContext,
            filters,
            workerOptions,
            accountOptions,
            activeFilterChips);
    }

    internal static IReadOnlyList<Guid> NormalizeIds(IReadOnlyList<Guid>? ids) =>
        ids is null or { Count: 0 }
            ? []
            : ids.Distinct().ToList();

    private static IReadOnlyList<EventFilterOptionViewModel> BuildWorkerOptions(IReadOnlyList<WorkerListItem> workers) =>
        workers
            .OrderBy(w => w.DisplayName)
            .Select(w => new EventFilterOptionViewModel
            {
                Value = w.Id.ToString(),
                Label = w.DisplayName
            })
            .ToList();

    private static IReadOnlyList<EventFilterOptionViewModel> BuildAccountOptions(
        IReadOnlyList<ResponseFilterAccountDto> accounts) =>
        accounts
            .OrderBy(a => a.AccountName)
            .Select(a => new EventFilterOptionViewModel
            {
                Value = a.AccountId.ToString(),
                Label = a.AccountName
            })
            .ToList();
}
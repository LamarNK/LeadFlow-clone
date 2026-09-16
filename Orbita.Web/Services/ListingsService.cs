using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class ListingsService(
    OrbitaApiClient api,
    IOfficeContext officeContext,
    IOptions<DesignPreviewOptions> previewOptions) : IListingsService
{
    public async Task<ListingsIndexViewModel> GetIndexAsync(
        string? searchQuery = null,
        string? tab = null,
        IReadOnlyList<Guid>? workerIds = null,
        IReadOnlyList<Guid>? accountIds = null,
        IReadOnlyList<string>? subProfileIds = null,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default)
    {
        pageSize = ListPageSizeDefaults.Normalize(pageSize, ListPageSizeDefaults.Listings);
        var normalizedTab = ListingsIndexBuilder.NormalizeTab(tab);
        var selectedWorkers = ResponseCatalogFilterValues.MergeIds(workerIds, null);
        var selectedAccounts = ResponseCatalogFilterValues.MergeIds(accountIds, null);
        var selectedSubProfiles = ResponseCatalogFilterValues.MergeValues(subProfileIds, null);

        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildListingsIndexViewModel(
                searchQuery,
                normalizedTab,
                page,
                pageSize.Value,
                sort,
                sortDir,
                selectedWorkers,
                selectedAccounts,
                selectedSubProfiles);
        }

        var workersTask = api.GetWorkersAsync(ct);
        var listingsTask = api.GetListingsAsync(
            searchQuery,
            ct,
            selectedWorkers,
            selectedAccounts,
            selectedSubProfiles,
            normalizedTab,
            page,
            pageSize.Value,
            sort,
            sortDir);
        var accountsTask = api.GetOfficeAccountsAsync(workerId: null, ct);
        await Task.WhenAll(workersTask, listingsTask, accountsTask);

        var workers = ResponsesIndexBuilder.BuildWorkerOptions(await workersTask ?? []);
        var listings = await listingsTask ?? new AvitoAdListingListResponse([], new AvitoAdListingSummary(0, 0, 0, 0, 0), 0, []);
        var officeAccounts = await accountsTask ?? [];

        var accountOptions = officeAccounts
            .OrderBy(x => x.Account.DisplayName)
            .Select(x => new EventFilterOptionViewModel
            {
                Value = x.Account.AccountId.ToString(),
                Label = x.Account.DisplayName
            })
            .ToList();

        var subOptions = officeAccounts
            .Where(x => selectedWorkers.Count == 0 || selectedWorkers.Contains(x.WorkerId))
            .Where(x => selectedAccounts.Count == 0 || selectedAccounts.Contains(x.Account.AccountId))
            .SelectMany(x => x.Account.SubProfiles ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(x => x.Name)
            .Select(x => new EventFilterOptionViewModel
            {
                Value = x.Id,
                Label = string.IsNullOrWhiteSpace(x.Name) ? x.Id : x.Name
            })
            .ToList();

        var scopeMetrics = listings.ScopeSummaries.ToDictionary(
            x => (x.WorkerId, x.AccountId, x.AvitoSubProfileId ?? string.Empty));
        var accountMetrics = listings.ScopeSummaries
            .GroupBy(x => (x.WorkerId, x.AccountId))
            .ToDictionary(
                group => group.Key,
                group => new ListingStatusMetricsViewModel
                {
                    ActiveCount = group.Sum(x => x.ActiveCount),
                    UnpublishedCount = group.Sum(x => x.UnpublishedCount),
                    ErrorCount = group.Sum(x => x.ErrorCount)
                });
        var accountScopes = officeAccounts
            .OrderBy(x => x.Account.DisplayName)
            .Select(x => new ListingAccountScopeViewModel
            {
                WorkerId = x.WorkerId,
                AccountId = x.Account.AccountId,
                AccountName = x.Account.DisplayName,
                Metrics = accountMetrics.GetValueOrDefault((x.WorkerId, x.Account.AccountId))
                    ?? new ListingStatusMetricsViewModel(),
                SubProfiles = (x.Account.SubProfiles ?? [])
                    .Where(sub => sub.IsEnabledInPanel)
                    .OrderBy(sub => sub.Name)
                    .Select(sub => new ListingSubProfileScopeViewModel
                    {
                        Id = sub.Id,
                        Name = string.IsNullOrWhiteSpace(sub.Name) ? sub.Id : sub.Name,
                        Metrics = scopeMetrics.TryGetValue((x.WorkerId, x.Account.AccountId, sub.Id), out var metrics)
                            ? new ListingStatusMetricsViewModel
                            {
                                ActiveCount = metrics.ActiveCount,
                                UnpublishedCount = metrics.UnpublishedCount,
                                ErrorCount = metrics.ErrorCount
                            }
                            : new ListingStatusMetricsViewModel()
                    })
                    .ToList()
            })
            .ToList();

        return ListingsIndexBuilder.Build(
            listings.Items,
            listings.Summary,
            searchQuery,
            normalizedTab,
            page,
            pageSize.Value,
            sort,
            sortDir,
            selectedWorkers,
            selectedAccounts,
            selectedSubProfiles,
            workers,
            accountOptions,
            subOptions,
            officeContext,
            totalItems: listings.Total,
            itemsArePaged: true,
            accountScopes: accountScopes);
    }
}

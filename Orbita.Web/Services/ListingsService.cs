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
        var selectedWorkers = ResponseCatalogFilterValues.MergeIds(workerIds, null);
        var selectedAccounts = ResponseCatalogFilterValues.MergeIds(accountIds, null);
        var selectedSubProfiles = ResponseCatalogFilterValues.MergeValues(subProfileIds, null);

        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildListingsIndexViewModel(
                searchQuery,
                tab,
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
            selectedSubProfiles);
        var accountsTask = api.GetOfficeAccountsAsync(workerId: null, ct);
        await Task.WhenAll(workersTask, listingsTask, accountsTask);

        var workers = ResponsesIndexBuilder.BuildWorkerOptions(await workersTask ?? []);
        var listings = await listingsTask ?? new AvitoAdListingListResponse([], new AvitoAdListingSummary(0, 0, 0, 0, 0), 0);
        var officeAccounts = await accountsTask ?? [];

        var accountOptions = officeAccounts
            .OrderBy(x => x.Account.DisplayName)
            .Select(x => new EventFilterOptionViewModel
            {
                Value = x.Account.AccountId.ToString(),
                Label = x.Account.DisplayName
            })
            .ToList();

        var subOptions = listings.Items
            .GroupBy(x => x.AvitoSubProfileId)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .OrderBy(g => g.First().SubProfileName)
            .Select(g => new EventFilterOptionViewModel
            {
                Value = g.Key,
                Label = string.IsNullOrWhiteSpace(g.First().SubProfileName) ? g.Key : g.First().SubProfileName
            })
            .ToList();

        return ListingsIndexBuilder.Build(
            listings.Items,
            listings.Summary,
            searchQuery,
            tab,
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
            officeContext);
    }
}

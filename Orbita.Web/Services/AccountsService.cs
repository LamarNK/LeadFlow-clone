using Microsoft.Extensions.Options;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class AccountsService(
    OrbitaApiClient api,
    IOfficeContext officeContext,
    IOptions<DesignPreviewOptions> previewOptions) : IAccountsService
{
    public async Task<AccountsIndexViewModel> GetIndexAsync(
        string? searchQuery = null,
        string? tab = null,
        Guid? workerId = null,
        string? groupId = null,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default)
    {
        pageSize = ListPageSizeDefaults.Normalize(pageSize, ListPageSizeDefaults.Accounts);

        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildAccountsIndexViewModel(
                searchQuery,
                tab,
                page,
                pageSize.Value,
                sort,
                sortDir,
                officeContext.ShowOfficeColumn,
                workerId,
                groupId);
        }

        var workersTask = api.GetWorkersAsync(ct);
        var officeAccountsTask = api.GetOfficeAccountsAsync(workerId, ct);
        await Task.WhenAll(workersTask, officeAccountsTask);

        var allWorkers = await workersTask ?? [];
        var workerOptions = ResponsesIndexBuilder.BuildWorkerOptions(allWorkers);
        var officeAccounts = await officeAccountsTask ?? [];

        var rows = officeAccounts
            .Select(item => AccountsIndexBuilder.MapAccount(
                item.Account,
                item.WorkerId,
                item.WorkerDisplayName,
                item.OfficeName,
                item.Balance?.TotalBalance ?? 0,
                item.Balance,
                item.CurrentActivity,
                item.WorkerIsOnline,
                item.ActiveAccounts ?? item.CurrentActivity?.ActiveAccounts))
            .ToList();

        return AccountsIndexBuilder.Build(
            rows,
            searchQuery,
            tab,
            page,
            sort,
            sortDir,
            pageSize: pageSize.Value,
            showOfficeColumn: officeContext.ShowOfficeColumn,
            officeContext: officeContext,
            workerId: workerId,
            workers: workerOptions,
            groupId: groupId);
    }
}
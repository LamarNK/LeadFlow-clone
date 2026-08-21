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

        var allWorkers = await api.GetWorkersAsync(ct) ?? [];
        var workerOptions = ResponsesIndexBuilder.BuildWorkerOptions(allWorkers);

        var workers = workerId is Guid wid
            ? allWorkers.Where(w => w.Id == wid).ToList()
            : allWorkers;

        var rows = new List<AccountRowViewModel>();
        foreach (var worker in workers)
        {
            var accounts = await api.GetWorkerAccountsAsync(worker.Id, ct);
            if (accounts is null) continue;

            var workerDetail = await api.GetWorkerAsync(worker.Id, ct);
            foreach (var account in accounts)
            {
                var balanceDetail = workerDetail?.Balances.FirstOrDefault(b => b.AccountId == account.AccountId);
                var balance = balanceDetail?.TotalBalance ?? 0;
                rows.Add(AccountsIndexBuilder.MapAccount(
                    account,
                    worker.Id,
                    worker.DisplayName,
                    worker.OfficeName,
                    balance,
                    balanceDetail,
                    worker.CurrentActivity,
                    worker.IsOnline,
                    worker.ActiveAccounts ?? worker.CurrentActivity?.ActiveAccounts));
            }
        }

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
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
        int page = 1,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
            return DesignPreviewData.BuildAccountsIndexViewModel(
                searchQuery,
                tab,
                page,
                AccountsIndexBuilder.DefaultPageSize,
                sort,
                sortDir,
                officeContext.ShowOfficeColumn);

        var rows = new List<AccountRowViewModel>();
        var workers = await api.GetWorkersAsync(ct) ?? [];

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
            showOfficeColumn: officeContext.ShowOfficeColumn,
            officeContext: officeContext);
    }
}
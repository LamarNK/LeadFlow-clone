using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public sealed class AccountsService(OrbitaApiClient api) : IAccountsService
{
    public async Task<AccountsIndexViewModel> GetIndexAsync(CancellationToken ct = default)
    {
        var rows = new List<AccountRowViewModel>();
        var workers = await api.GetWorkersAsync(ct) ?? [];

        foreach (var worker in workers)
        {
            var accounts = await api.GetWorkerAccountsAsync(worker.Id, ct);
            if (accounts is null) continue;

            foreach (var account in accounts)
            {
                rows.Add(new AccountRowViewModel
                {
                    AccountName = account.DisplayName,
                    WorkerName = worker.DisplayName,
                    Status = account.Status,
                    ActiveAds = account.ActiveAdsCount,
                    Blocked = account.BlockedCount,
                    LastError = account.LastErrorMessage
                });
            }
        }

        return new AccountsIndexViewModel { Rows = rows };
    }
}
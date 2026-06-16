using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LeadFlow.Data;
using LeadFlow.Models;

namespace LeadFlow.ViewModels;

public sealed partial class BalanceDetailsViewModel : ObservableObject
{
    private readonly AppRepository _repository;

    [ObservableProperty]
    private string totalBalanceDisplay = "";

    [ObservableProperty]
    private string summaryLine = "";

    [ObservableProperty]
    private bool hasData;

    public ObservableCollection<AccountBarItem> Accounts { get; } = new();

    public BalanceDetailsViewModel(AppRepository repository)
    {
        _repository = repository;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var allAccounts = await _repository.GetAccountBalancesAsync(ct);

        var total = allAccounts.Sum(a => a.TotalBalance);
        var allBalances = allAccounts.SelectMany(a => a.SubProfiles).Select(s => s.Balance).Where(b => b.HasValue).Select(b => b!.Value).ToList();

        TotalBalanceDisplay = $"{total:N2} ₽";

        var accountCount = allAccounts.Count(a => a.HasBalance);
        var subCount = allBalances.Count;
        var min = allBalances.Count > 0 ? allBalances.Min() : 0m;
        var max = allBalances.Count > 0 ? allBalances.Max() : 0m;
        var avg = allBalances.Count > 0 ? allBalances.Average() : 0m;
        SummaryLine = $"{accountCount} акк. · {subCount} суб. · мин {min:N0} · макс {max:N0} · средн {avg:N0}";
        HasData = allAccounts.Count > 0;

        var globalMax = total > 0 ? total : 1m;

        Accounts.Clear();
        foreach (var acc in allAccounts)
        {
            var accMax = acc.SubProfiles.Count > 0
                ? acc.SubProfiles.Max(s => s.Balance ?? 0m)
                : 1m;

            var accBar = new AccountBarItem
            {
                AccountName = acc.AccountName,
                TotalBalance = acc.TotalBalance,
                TotalBalanceDisplay = acc.TotalBalanceDisplay,
                BarWidth = (double)(acc.TotalBalance / globalMax),
                SubProfileBars = acc.SubProfiles.Select(sp => new SubProfileBarItem
                {
                    Name = sp.SubProfileName,
                    Balance = sp.Balance ?? 0m,
                    BalanceDisplay = sp.BalanceDisplay,
                    BarWidth = accMax > 0m ? (double)((sp.Balance ?? 0m) / accMax) : 0.0
                }).ToList()
            };

            Accounts.Add(accBar);
        }
    }
}

public sealed class AccountBarItem
{
    public string AccountName { get; init; } = "";
    public decimal TotalBalance { get; init; }
    public string TotalBalanceDisplay { get; init; } = "";
    public double BarWidth { get; init; }
    public List<SubProfileBarItem> SubProfileBars { get; init; } = new();
}

public sealed class SubProfileBarItem
{
    public string Name { get; init; } = "";
    public decimal Balance { get; init; }
    public string BalanceDisplay { get; init; } = "";
    public double BarWidth { get; init; }
}

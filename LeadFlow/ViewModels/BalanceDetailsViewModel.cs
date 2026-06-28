using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;



namespace LeadFlow.ViewModels;

public sealed partial class BalanceDetailsViewModel : ObservableObject
{
    private readonly AppRepository _repository;

    [ObservableProperty]
    private string totalBalanceDisplay = "";

    [ObservableProperty]
    private string summaryLine = "";

    [ObservableProperty]
    private string lowBalanceHint = "";

    [ObservableProperty]
    private bool hasData;

    [ObservableProperty]
    private string searchQuery = "";

    [ObservableProperty]
    private int visibleAccountCount;

    [ObservableProperty]
    private int lowBalanceAccountCount;

    public ObservableCollection<AccountBarItem> Accounts { get; } = new();

    public ICollectionView AccountsView { get; }

    public BalanceDetailsViewModel(AppRepository repository)
    {
        _repository = repository;
        AccountsView = CollectionViewSource.GetDefaultView(Accounts);
        AccountsView.Filter = FilterAccount;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var allAccounts = await _repository.GetAccountBalancesAsync(ct);

        var total = allAccounts.Sum(a => a.TotalBalance);
        var allBalances = allAccounts
            .SelectMany(a => a.SubProfiles)
            .Select(s => s.Balance)
            .Where(b => b.HasValue)
            .Select(b => b!.Value)
            .ToList();

        TotalBalanceDisplay = $"{total:N2} ₽";

        var accountCount = allAccounts.Count;
        var subCount = allAccounts.Sum(a => a.SubProfileCount);
        var withBalance = allBalances.Count;
        var min = allBalances.Count > 0 ? allBalances.Min() : 0m;
        var max = allBalances.Count > 0 ? allBalances.Max() : 0m;
        var avg = allBalances.Count > 0 ? allBalances.Average() : 0m;
        SummaryLine = $"{accountCount} акк. · {subCount} субпроф. · с балансом {withBalance} · мин {min:N0} · макс {max:N0} · средн {avg:N0}";

        LowBalanceAccountCount = allAccounts.Count(a => a.HasLowBalance);
        LowBalanceHint = LowBalanceAccountCount > 0
            ? $"Ниже {BalanceDisplayRules.LowBalanceThresholdRub:N0} ₽: {LowBalanceAccountCount} акк."
            : string.Empty;

        HasData = allAccounts.Count > 0;

        var globalMax = total > 0 ? total : 1m;

        Accounts.Clear();
        foreach (var acc in allAccounts)
        {
            var accMax = acc.SubProfiles.Count > 0
                ? acc.SubProfiles.Max(s => s.Balance ?? 0m)
                : 1m;
            if (accMax == 0m)
            {
                accMax = 1m;
            }

            var subBars = acc.SubProfiles.Select(sp => new SubProfileBarItem
            {
                Name = sp.SubProfileName,
                Balance = sp.Balance ?? 0m,
                BalanceDisplay = sp.BalanceDisplay,
                IsLowBalance = sp.IsLowBalance,
                BarWidth = accMax > 0m ? (double)((sp.Balance ?? 0m) / accMax) : 0.0
            }).ToList();

            Accounts.Add(new AccountBarItem
            {
                AccountName = acc.AccountName,
                TotalBalance = acc.TotalBalance,
                TotalBalanceDisplay = acc.TotalBalanceDisplay,
                BarWidth = (double)(acc.TotalBalance / globalMax),
                SubProfileCount = acc.SubProfileCount,
                HasLowBalance = acc.HasLowBalance,
                IsExpanded = acc.SubProfileCount <= BalanceDisplayRules.CollapseSubProfilesAbove,
                SubProfileBars = subBars
            });
        }

        RefreshFilter();
    }

    partial void OnSearchQueryChanged(string value) => RefreshFilter();

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var account in Accounts)
        {
            account.IsExpanded = true;
        }
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var account in Accounts)
        {
            account.IsExpanded = false;
        }
    }

    [RelayCommand]
    private void ExpandLowBalanceOnly()
    {
        foreach (var account in Accounts)
        {
            account.IsExpanded = account.HasLowBalance;
        }
    }

    private void RefreshFilter()
    {
        AccountsView.Refresh();
        VisibleAccountCount = AccountsView.Cast<object>().Count();
    }

    private bool FilterAccount(object item)
    {
        if (item is not AccountBarItem account)
        {
            return false;
        }

        var query = SearchQuery?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        return account.AccountName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || account.SubProfileBars.Any(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}

public partial class AccountBarItem : ObservableObject
{
    public string AccountName { get; init; } = "";
    public decimal TotalBalance { get; init; }
    public string TotalBalanceDisplay { get; init; } = "";
    public double BarWidth { get; init; }
    public int SubProfileCount { get; init; }
    public bool HasLowBalance { get; init; }
    public List<SubProfileBarItem> SubProfileBars { get; init; } = new();

    [ObservableProperty]
    private bool isExpanded;
}

public sealed class SubProfileBarItem
{
    public string Name { get; init; } = "";
    public decimal Balance { get; init; }
    public string BalanceDisplay { get; init; } = "";
    public bool IsLowBalance { get; init; }
    public double BarWidth { get; init; }
}
using Orbita.Contracts;
using Orbita.Web.Formatting;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class AccountsIndexBuilder
{
    public const int DefaultPageSize = 10;

    private static readonly AccountTabViewModel[] TabDefinitions =
    [
        new() { Id = "all", Label = "Все аккаунты" },
        new() { Id = "active", Label = "Активные" },
        new() { Id = "inactive", Label = "Неактивные" },
        new() { Id = "blocked", Label = "Заблокированные" },
        new() { Id = "errors", Label = "С ошибками" }
    ];

    public static AccountsIndexViewModel Build(
        IReadOnlyList<AccountRowViewModel> allRows,
        string? searchQuery,
        string? tab,
        int page,
        string? sort = null,
        string? sortDir = null,
        int pageSize = DefaultPageSize,
        bool showOfficeColumn = false,
        IOfficeContext? officeContext = null)
    {
        page = Math.Max(1, page);
        tab = NormalizeTab(tab);
        var tableSort = TableSort.Parse(sort, sortDir, TableSort.Accounts.Default, TableSort.Accounts.Columns);

        var filtered = FilterRows(allRows, searchQuery, tab);
        var sorted = TableSort.Accounts.Apply(filtered, tableSort).ToList();
        var total = sorted.Count;
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var summary = Summarize(allRows);

        var header = PageHeaderBuilder.AccountsList();
        if (officeContext is not null)
        {
            header = PageHeaderBuilder.WithOfficeScope(header, officeContext);
        }

        return new AccountsIndexViewModel
        {
            Header = header,
            SearchQuery = searchQuery,
            ActiveTab = tab,
            Tabs = TabDefinitions,
            KpiCards = BuildKpiCards(summary),
            Accounts = paged,
            Pagination = new PaginationViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalItems = total
            },
            Sort = tableSort,
            ShowOfficeColumn = showOfficeColumn
        };
    }

    public static AccountRowViewModel MapAccount(
        WorkerAccountDto account,
        Guid workerId,
        string workerName,
        string officeName = "",
        decimal balance = 0,
        WorkerBalanceDto? balanceDetail = null,
        WorkerActivityDto? workerActivity = null,
        bool workerIsOnline = false,
        IReadOnlyList<WorkerActiveAccountDto>? activeAccounts = null)
    {
        var processing = WorkerActivityPresenter.PresentForAccount(
            workerActivity,
            workerIsOnline,
            account.AccountId,
            activeAccounts);
        var (label, tone) = AccountStatusMapper.ForAccountsPage(account.Status, account.IsEnabledInPanel);
        var responses = account.TodayResponses;
        var duplicates = account.TodayDuplicates;
        var errors = account.TodayEventErrors;
        var unique = Math.Max(0, responses - duplicates);
        var hasError = !string.IsNullOrWhiteSpace(account.LastErrorMessage);
        var subProfiles = SubProfileViewModelMapper.Map(account.SubProfiles, balanceDetail?.SubProfiles);
        var walletBalance = balanceDetail?.TotalWalletBalance ?? 0m;
        var durationHint = BalanceDisplay.ResolveAdvanceDurationHint(
            (balanceDetail?.SubProfiles ?? [])
                .Select(s => (s.Balance, s.AdvanceDurationText))
                .ToList());
        return new AccountRowViewModel
        {
            Id = account.AccountId,
            AccountName = account.DisplayName,
            WorkerId = workerId,
            WorkerName = workerName,
            OfficeName = officeName,
            StatusLabel = label,
            StatusTone = tone,
            Balance = balance,
            WalletBalance = walletBalance,
            BalanceBreakdown = SubProfileViewModelMapper.BuildBalanceBreakdown(subProfiles),
            BalanceSubtitle = BalanceDisplay.FormatAccountBreakdown(
                walletBalance > 0 ? walletBalance : null,
                durationHint),
            Responses = responses,
            UniqueResponses = unique,
            Errors = errors > 0 ? errors : hasError ? 1 : 0,
            LastActivityUtc = account.LastMonitoringAt,
            IsEnabledInPanel = account.IsEnabledInPanel,
            LastErrorMessage = AdsPowerErrorMessageNormalizer.NormalizeForDisplay(account.LastErrorMessage),
            SubProfiles = subProfiles,
            SubProfilesSummary = SubProfileViewModelMapper.BuildSummary(subProfiles),
            CanRefreshSubProfiles = !string.IsNullOrWhiteSpace(account.AdsPowerProfileId),
            IsSubProfilesRefreshPending = SubProfileViewModelMapper.IsRefreshPending(
                account.SubProfilesRefreshRequestedAtUtc,
                account.SubProfilesRefreshedAtUtc),
            IsProcessingNow = processing.IsProcessingNow,
            ProcessingLabel = processing.Label,
            ProcessingTone = processing.Tone,
            ProcessingSubProfileId = processing.SubProfileId
        };
    }

    private static IReadOnlyList<AccountRowViewModel> FilterRows(
        IReadOnlyList<AccountRowViewModel> rows,
        string? searchQuery,
        string tab)
    {
        IEnumerable<AccountRowViewModel> query = rows;

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var q = searchQuery.Trim();
            query = query.Where(a =>
                a.AccountName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || a.WorkerName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        query = tab switch
        {
            "active" => query.Where(a => a.StatusTone == "active"),
            "inactive" => query.Where(a => a.StatusTone == "inactive"),
            "blocked" => query.Where(a => a.StatusTone == "blocked"),
            "errors" => query.Where(HasErrors),
            _ => query
        };

        return query.ToList();
    }

    private static AccountsSummaryViewModel Summarize(IReadOnlyList<AccountRowViewModel> rows) => new()
    {
        Total = rows.Count,
        Active = rows.Count(a => a.StatusTone == "active"),
        Inactive = rows.Count(a => a.StatusTone == "inactive"),
        Blocked = rows.Count(a => a.StatusTone == "blocked"),
        Errors = rows.Count(HasErrors)
    };

    private static bool HasErrors(AccountRowViewModel account) =>
        account.StatusTone == "error" || account.Errors > 0;

    private static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(AccountsSummaryViewModel summary)
    {
        var total = Math.Max(1, summary.Total);
        string Pct(int value) => $"{value * 100.0 / total:0.#}%";

        return
        [
            new()
            {
                Key = "total",
                Href = KpiCardLinks.AccountsCard("total"),
                Label = "Всего аккаунтов",
                Value = summary.Total.ToString(),
                CountValue = summary.Total,
                Delta = "Все время",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-user",
                IconTone = "blue"
            },
            new()
            {
                Key = "active",
                Href = KpiCardLinks.AccountsCard("active"),
                Label = "Активны",
                Value = summary.Active.ToString(),
                CountValue = summary.Active,
                Delta = Pct(summary.Active),
                DeltaTone = "good",
                IconClass = "fa-solid fa-circle-check",
                IconTone = "green"
            },
            new()
            {
                Key = "inactive",
                Href = KpiCardLinks.AccountsCard("inactive"),
                Label = "Неактивны",
                Value = summary.Inactive.ToString(),
                CountValue = summary.Inactive,
                Delta = Pct(summary.Inactive),
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-circle",
                IconTone = "gray"
            },
            new()
            {
                Key = "blocked",
                Href = KpiCardLinks.AccountsCard("blocked"),
                Label = "Заблокированы",
                Value = summary.Blocked.ToString(),
                CountValue = summary.Blocked,
                Delta = Pct(summary.Blocked),
                DeltaTone = "bad",
                IconClass = "fa-solid fa-ban",
                IconTone = "orange"
            },
            new()
            {
                Key = "errors",
                Href = KpiCardLinks.AccountsCard("errors"),
                Label = "Ошибки",
                Value = summary.Errors.ToString(),
                CountValue = summary.Errors,
                Delta = Pct(summary.Errors),
                DeltaTone = summary.Errors > 0 ? "bad" : "good",
                IconClass = "fa-solid fa-triangle-exclamation",
                IconTone = "orange"
            }
        ];
    }

    private static string NormalizeTab(string? tab) =>
        TabDefinitions.Any(t => t.Id == tab) ? tab! : "all";
}
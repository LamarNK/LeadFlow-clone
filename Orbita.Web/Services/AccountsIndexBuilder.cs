using Orbita.Contracts;
using Orbita.Web.Formatting;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class AccountsIndexBuilder
{
    public const int DefaultPageSize = ListPageSizeDefaults.Accounts;

    private static readonly AccountTabViewModel[] TabDefinitions =
    [
        new() { Id = "all", Label = "Все аккаунты" },
        new() { Id = "active", Label = "Активные" },
        new() { Id = "inactive", Label = "Неактивные" },
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
        IOfficeContext? officeContext = null,
        Guid? workerId = null,
        IReadOnlyList<EventFilterOptionViewModel>? workers = null,
        string? groupId = null)
    {
        page = Math.Max(1, page);
        tab = NormalizeTab(tab);
        var tableSort = TableSort.Parse(sort, sortDir, TableSort.Accounts.Default, TableSort.Accounts.Columns);
        var normalizedGroupId = AdsPowerAccountGroupFilter.Normalize(groupId);

        var scopedRows = FilterByWorker(allRows, workerId);
        var groupOptions = AdsPowerAccountGroupFilter.BuildOptions(
            scopedRows.Select(a => (a.AdsPowerGroupId, a.AdsPowerGroupName)));
        var filtered = FilterRows(scopedRows, searchQuery, tab, normalizedGroupId);
        var sorted = TableSort.Accounts.Apply(filtered, tableSort).ToList();
        var total = sorted.Count;
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var summary = Summarize(scopedRows);

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
            ShowOfficeColumn = showOfficeColumn,
            WorkerId = workerId,
            Workers = workers ?? [],
            GroupId = normalizedGroupId,
            Groups = groupOptions,
            HasActiveFilters = !string.IsNullOrWhiteSpace(searchQuery)
                || tab != "all"
                || workerId.HasValue
                || normalizedGroupId is not null,
            ActiveFilterChips = FilterChipsBuilder.ForAccounts(
                searchQuery,
                tab,
                TabDefinitions,
                workerId,
                workers ?? [],
                pageSize,
                normalizedGroupId,
                groupOptions)
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
        var unique = Math.Max(0, responses - duplicates);
        var subProfiles = SubProfileViewModelMapper.Map(
            account.SubProfiles,
            balanceDetail?.SubProfiles,
            workerId,
            account.AccountId,
            workerIsOnline,
            workerActivity,
            activeAccounts);
        var lastErrorMessage = AdsPowerErrorMessageNormalizer.NormalizeForDisplay(account.LastErrorMessage);
        var errors = AccountErrorMetrics.ComputeErrorCount(
            account.TodayEventErrors,
            lastErrorMessage,
            subProfiles,
            tone);
        var errorHint = AccountErrorMetrics.ComputeErrorHint(
            account.TodayEventErrors,
            lastErrorMessage,
            subProfiles,
            tone);
        var walletBalance = balanceDetail?.TotalWalletBalance ?? 0m;
        var durationHint = BalanceDisplay.ResolveAdvanceDurationHint(
            (balanceDetail?.SubProfiles ?? [])
                .Select(s => (s.Balance, s.AdvanceDurationText))
                .ToList());
        var metricLinks = AccountMetricLinks.Hrefs(workerId, account.AccountId);
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
            Errors = errors,
            ResponsesLink = metricLinks.Responses,
            UniqueResponsesLink = metricLinks.UniqueResponses,
            ErrorsLink = metricLinks.Errors,
            LastActivityUtc = account.LastActivityUtc ?? account.LastMonitoringAt,
            IsEnabledInPanel = account.IsEnabledInPanel,
            HasAvitoCredentials = account.HasAvitoCredentials,
            AvitoLogin = account.AvitoLogin,
            LastErrorMessage = lastErrorMessage,
            ErrorHint = errorHint,
            SubProfiles = subProfiles,
            SubProfilesSummary = SubProfileViewModelMapper.BuildSummary(subProfiles),
            CanRefreshSubProfiles = !string.IsNullOrWhiteSpace(account.AdsPowerProfileId),
            IsSubProfilesRefreshPending = SubProfileViewModelMapper.IsRefreshPending(
                account.SubProfilesRefreshRequestedAtUtc,
                account.SubProfilesRefreshedAtUtc),
            IsProcessingNow = processing.IsProcessingNow,
            ProcessingLabel = processing.Label,
            ProcessingTone = processing.Tone,
            ProcessingSubProfileId = processing.SubProfileId,
            AdsPowerGroupId = account.AdsPowerGroupId,
            AdsPowerGroupName = account.AdsPowerGroupName
        };
    }

    private static IReadOnlyList<AccountRowViewModel> FilterByWorker(
        IReadOnlyList<AccountRowViewModel> rows,
        Guid? workerId)
    {
        if (workerId is not Guid wid)
        {
            return rows;
        }

        return rows.Where(a => a.WorkerId == wid).ToList();
    }

    private static IReadOnlyList<AccountRowViewModel> FilterRows(
        IReadOnlyList<AccountRowViewModel> rows,
        string? searchQuery,
        string tab,
        string? groupId)
    {
        IEnumerable<AccountRowViewModel> query = rows;

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            query = query.Where(a =>
                SearchQueryNormalizer.MatchesTokens(
                    searchQuery,
                    a.AccountName,
                    a.WorkerName,
                    a.AdsPowerGroupName));
        }

        if (!string.IsNullOrWhiteSpace(groupId))
        {
            query = query.Where(a => AdsPowerAccountGroupFilter.Matches(groupId, a.AdsPowerGroupId));
        }

        query = tab switch
        {
            "active" => query.Where(IsActiveInPanel),
            "inactive" => query.Where(a => a.StatusTone == "inactive"),
            "errors" => query.Where(HasErrors),
            _ => query
        };

        return query.ToList();
    }

    private static AccountsSummaryViewModel Summarize(IReadOnlyList<AccountRowViewModel> rows) => new()
    {
        Total = rows.Count,
        Active = rows.Count(IsActiveInPanel),
        Inactive = rows.Count(a => a.StatusTone == "inactive"),
        Errors = rows.Count(HasErrors)
    };

    /// <summary>
    /// Вкладка «Активные»: включён в панели и не отключён по статусу воркера.
    /// Аккаунт с ошибкой остаётся здесь, если он включён — ошибка видна во вкладке «С ошибками».
    /// </summary>
    private static bool IsActiveInPanel(AccountRowViewModel account) =>
        account.IsEnabledInPanel
        && account.StatusTone != "inactive";

    private static bool HasErrors(AccountRowViewModel account) =>
        AccountErrorMetrics.HasErrors(account);

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
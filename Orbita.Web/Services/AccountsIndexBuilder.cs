using Orbita.Contracts;
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
        int pageSize = DefaultPageSize)
    {
        page = Math.Max(1, page);
        tab = NormalizeTab(tab);

        var filtered = FilterRows(allRows, searchQuery, tab);
        var total = filtered.Count;
        var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var summary = Summarize(allRows);

        return new AccountsIndexViewModel
        {
            Header = PageHeaderBuilder.AccountsList(),
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
            }
        };
    }

    public static AccountRowViewModel MapAccount(
        WorkerAccountDto account,
        Guid workerId,
        string workerName,
        decimal balance = 0,
        int responses = 0,
        int errors = 0)
    {
        var (label, tone) = MapStatus(account.Status, account.IsEnabled);
        var unique = Math.Max(0, responses - errors / 2);
        var hasError = !string.IsNullOrWhiteSpace(account.LastErrorMessage);
        return new AccountRowViewModel
        {
            Id = account.AccountId,
            AccountName = account.DisplayName,
            WorkerId = workerId,
            WorkerName = workerName,
            StatusLabel = label,
            StatusTone = tone,
            Balance = balance,
            Responses = responses,
            UniqueResponses = unique,
            Errors = errors > 0 ? errors : hasError ? 1 : 0,
            LastActivityUtc = account.LastMonitoringAt,
            IsEnabledInPanel = account.IsEnabledInPanel,
            LastErrorMessage = account.LastErrorMessage
        };
    }

    public static (string Label, string Tone) MapStatus(string status, bool isEnabled)
    {
        if (status.Equals("Blocked", StringComparison.OrdinalIgnoreCase))
            return ("Заблокирован", "blocked");

        if (status.Equals("Error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("RequiresLogin", StringComparison.OrdinalIgnoreCase)
            || status.Equals("RequiresManualAction", StringComparison.OrdinalIgnoreCase))
            return ("Ошибка", "error");

        if (!isEnabled
            || status.Equals("Paused", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Offline", StringComparison.OrdinalIgnoreCase))
            return ("Неактивен", "inactive");

        return ("Активен", "active");
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
            "errors" => query.Where(a => a.StatusTone == "error" || !string.IsNullOrWhiteSpace(a.LastErrorMessage)),
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
        Errors = rows.Count(a => a.StatusTone == "error")
    };

    private static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(AccountsSummaryViewModel summary)
    {
        var total = Math.Max(1, summary.Total);
        string Pct(int value) => $"{value * 100.0 / total:0.#}%";

        return
        [
            new()
            {
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
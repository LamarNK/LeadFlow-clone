using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class FilterChipsBuilder
{
    public static string BuildUrl(string path, params (string Key, string? Value)[] pairs)
    {
        var query = string.Join(
            '&',
            pairs
                .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));

        return string.IsNullOrEmpty(query) ? path : $"{path}?{query}";
    }

    private static string? OptionLabel(IReadOnlyList<EventFilterOptionViewModel> options, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase))?.Label;
    }

    private static string? PageSizeQuery(int pageSize, int defaultSize) =>
        pageSize == defaultSize ? null : pageSize.ToString();

    private static string BuildListUrl(
        string path,
        int pageSize,
        int defaultPageSize,
        params (string Key, string? Value)[] pairs) =>
        BuildListUrl(path, pageSize, defaultPageSize, (IEnumerable<(string Key, string? Value)>)pairs);

    private static string BuildListUrl(
        string path,
        int pageSize,
        int defaultPageSize,
        IEnumerable<(string Key, string? Value)> pairs)
    {
        var allPairs = new List<(string Key, string? Value)>
        {
            ("pageSize", PageSizeQuery(pageSize, defaultPageSize))
        };
        allPairs.AddRange(pairs);
        return BuildUrl(path, allPairs.ToArray());
    }

    public static IReadOnlyList<ActiveFilterChipViewModel> ForEvents(
        EventsFilterViewModel filters,
        IReadOnlyList<EventFilterOptionViewModel> eventTypes,
        IReadOnlyList<EventFilterOptionViewModel> workers,
        IReadOnlyList<EventFilterOptionViewModel> accounts,
        IReadOnlyList<EventFilterOptionViewModel> levels,
        string? journalView = null,
        int pageSize = ListPageSizeDefaults.Events)
    {
        const string path = "/Events";
        var chips = new List<ActiveFilterChipViewModel>();
        var viewPair = string.IsNullOrWhiteSpace(journalView) || journalView == "all"
            ? Array.Empty<(string Key, string? Value)>()
            : new[] { ("view", (string?)journalView) };

        if (!string.IsNullOrWhiteSpace(filters.SearchQuery))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Поиск: {filters.SearchQuery}",
                RemoveUrl = BuildListUrl(path, pageSize, ListPageSizeDefaults.Events,
                    viewPair.Concat([
                        ("type", filters.Type),
                        ("workerId", filters.WorkerId?.ToString()),
                        ("accountId", filters.AccountId?.ToString()),
                        ("level", filters.Level),
                        ("page", "1")
                    ]))
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.Type))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = OptionLabel(eventTypes, filters.Type) ?? filters.Type,
                RemoveUrl = BuildListUrl(path, pageSize, ListPageSizeDefaults.Events,
                    viewPair.Concat([
                        ("q", filters.SearchQuery),
                        ("workerId", filters.WorkerId?.ToString()),
                        ("accountId", filters.AccountId?.ToString()),
                        ("level", filters.Level),
                        ("page", "1")
                    ]))
            });
        }

        if (filters.WorkerId is Guid workerId)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Воркер: {OptionLabel(workers, workerId.ToString()) ?? workerId.ToString()[..8]}",
                RemoveUrl = BuildListUrl(path, pageSize, ListPageSizeDefaults.Events,
                    viewPair.Concat([
                        ("q", filters.SearchQuery),
                        ("type", filters.Type),
                        ("accountId", filters.AccountId?.ToString()),
                        ("level", filters.Level),
                        ("page", "1")
                    ]))
            });
        }

        if (filters.AccountId is Guid accountId)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Аккаунт: {OptionLabel(accounts, accountId.ToString()) ?? accountId.ToString()[..8]}",
                RemoveUrl = BuildListUrl(path, pageSize, ListPageSizeDefaults.Events,
                    viewPair.Concat([
                        ("q", filters.SearchQuery),
                        ("type", filters.Type),
                        ("workerId", filters.WorkerId?.ToString()),
                        ("level", filters.Level),
                        ("page", "1")
                    ]))
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.Level))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = OptionLabel(levels, filters.Level) ?? filters.Level,
                RemoveUrl = BuildListUrl(path, pageSize, ListPageSizeDefaults.Events,
                    viewPair.Concat([
                        ("q", filters.SearchQuery),
                        ("type", filters.Type),
                        ("workerId", filters.WorkerId?.ToString()),
                        ("accountId", filters.AccountId?.ToString()),
                        ("page", "1")
                    ]))
            });
        }

        return chips;
    }

    public static IReadOnlyList<ActiveFilterChipViewModel> ForErrors(
        ErrorsFilterViewModel filters,
        IReadOnlyList<EventFilterOptionViewModel> severityOptions,
        IReadOnlyList<EventFilterOptionViewModel> errorTypes,
        IReadOnlyList<EventFilterOptionViewModel> workers,
        IReadOnlyList<EventFilterOptionViewModel> accounts,
        int pageSize = ListPageSizeDefaults.Errors)
    {
        const string path = "/Events";
        var chips = new List<ActiveFilterChipViewModel>();

        if (!string.IsNullOrWhiteSpace(filters.SearchQuery))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Поиск: {filters.SearchQuery}",
                RemoveUrl = BuildListUrl(path, pageSize, ListPageSizeDefaults.Errors, [
                    ("view", "errors"),
                    ("severity", filters.Severity),
                    ("type", filters.Type),
                    ("workerId", filters.WorkerId?.ToString()),
                    ("accountId", filters.AccountId?.ToString()),
                    ("page", "1")
                ])
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.Severity))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = OptionLabel(severityOptions, filters.Severity) ?? filters.Severity,
                RemoveUrl = BuildListUrl(path, pageSize, ListPageSizeDefaults.Errors,
                    ("view", "errors"),
                    ("q", filters.SearchQuery),
                    ("type", filters.Type),
                    ("workerId", filters.WorkerId?.ToString()),
                    ("accountId", filters.AccountId?.ToString()),
                    ("page", "1"))
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.Type))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = OptionLabel(errorTypes, filters.Type) ?? filters.Type,
                RemoveUrl = BuildListUrl(path, pageSize, ListPageSizeDefaults.Errors,
                    ("view", "errors"),
                    ("q", filters.SearchQuery),
                    ("severity", filters.Severity),
                    ("workerId", filters.WorkerId?.ToString()),
                    ("accountId", filters.AccountId?.ToString()),
                    ("page", "1"))
            });
        }

        if (filters.WorkerId is Guid workerId)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Воркер: {OptionLabel(workers, workerId.ToString()) ?? workerId.ToString()[..8]}",
                RemoveUrl = BuildListUrl(path, pageSize, ListPageSizeDefaults.Errors,
                    ("view", "errors"),
                    ("q", filters.SearchQuery),
                    ("severity", filters.Severity),
                    ("type", filters.Type),
                    ("accountId", filters.AccountId?.ToString()),
                    ("page", "1"))
            });
        }

        if (filters.AccountId is Guid accountId)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Аккаунт: {OptionLabel(accounts, accountId.ToString()) ?? accountId.ToString()[..8]}",
                RemoveUrl = BuildListUrl(path, pageSize, ListPageSizeDefaults.Errors,
                    ("view", "errors"),
                    ("q", filters.SearchQuery),
                    ("severity", filters.Severity),
                    ("type", filters.Type),
                    ("workerId", filters.WorkerId?.ToString()),
                    ("page", "1"))
            });
        }

        return chips;
    }

    private static (string Key, string? Value)[] ResponsesFilterPairs(
        ResponsesFilterViewModel filters,
        DashboardPeriod period,
        params (string Key, string? Value)[] overrides)
    {
        var pairs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["from"] = period.FromIso,
            ["to"] = period.ToIso,
            ["tz"] = period.TimeZoneOffsetMinutes.ToString(),
            ["status"] = filters.Status,
            ["workerId"] = filters.WorkerId?.ToString(),
            ["accountId"] = filters.AccountId?.ToString(),
            ["bitrixDestination"] = filters.BitrixDestination,
            ["gender"] = filters.Gender,
            ["ageFrom"] = filters.AgeFrom?.ToString(),
            ["ageTo"] = filters.AgeTo?.ToString(),
            ["vacancy"] = filters.VacancyQuery,
            ["search"] = filters.SearchQuery,
            ["page"] = "1"
        };

        foreach (var (key, value) in overrides)
        {
            pairs[key] = value;
        }

        return pairs.Select(p => (p.Key, p.Value)).ToArray();
    }

    public static IReadOnlyList<ActiveFilterChipViewModel> ForResponses(
        ResponsesFilterViewModel filters,
        DashboardPeriod period,
        IReadOnlyList<EventFilterOptionViewModel> statuses,
        IReadOnlyList<EventFilterOptionViewModel> workers,
        IReadOnlyList<EventFilterOptionViewModel> accounts,
        IReadOnlyList<EventFilterOptionViewModel> bitrixDestinations,
        IReadOnlyList<EventFilterOptionViewModel> genders,
        int pageSize = ListPageSizeDefaults.Responses)
    {
        const string path = "/Responses";
        var chips = new List<ActiveFilterChipViewModel>();

        if (!period.IsTodayOnly && !period.IsAllTime)
        {
            var today = period.LocalToday.ToString("yyyy-MM-dd");
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Период: {period.Label}",
                RemoveUrl = BuildListUrl(
                    path,
                    pageSize,
                    ListPageSizeDefaults.Responses,
                    ResponsesFilterPairs(
                        filters,
                        period,
                        ("from", today),
                        ("to", today)))
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.SearchQuery))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Поиск: {filters.SearchQuery}",
                RemoveUrl = BuildListUrl(
                    path,
                    pageSize,
                    ListPageSizeDefaults.Responses,
                    ResponsesFilterPairs(filters, period, ("search", null)))
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = OptionLabel(statuses, filters.Status) ?? filters.Status,
                RemoveUrl = BuildListUrl(
                    path,
                    pageSize,
                    ListPageSizeDefaults.Responses,
                    ResponsesFilterPairs(filters, period, ("status", null)))
            });
        }

        if (filters.WorkerId is Guid workerId)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Воркер: {OptionLabel(workers, workerId.ToString()) ?? workerId.ToString()[..8]}",
                RemoveUrl = BuildListUrl(
                    path,
                    pageSize,
                    ListPageSizeDefaults.Responses,
                    ResponsesFilterPairs(filters, period, ("workerId", null)))
            });
        }

        if (filters.AccountId is Guid accountId)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Аккаунт: {OptionLabel(accounts, accountId.ToString()) ?? accountId.ToString()[..8]}",
                RemoveUrl = BuildListUrl(
                    path,
                    pageSize,
                    ListPageSizeDefaults.Responses,
                    ResponsesFilterPairs(filters, period, ("accountId", null)))
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.BitrixDestination))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Битрикс: {OptionLabel(bitrixDestinations, filters.BitrixDestination) ?? filters.BitrixDestination}",
                RemoveUrl = BuildListUrl(
                    path,
                    pageSize,
                    ListPageSizeDefaults.Responses,
                    ResponsesFilterPairs(filters, period, ("bitrixDestination", null)))
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.Gender))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Пол: {OptionLabel(genders, filters.Gender) ?? CandidateGenders.FormatFilterLabel(filters.Gender)}",
                RemoveUrl = BuildListUrl(
                    path,
                    pageSize,
                    ListPageSizeDefaults.Responses,
                    ResponsesFilterPairs(filters, period, ("gender", null)))
            });
        }

        if (filters.AgeFrom.HasValue || filters.AgeTo.HasValue)
        {
            var ageLabel = (filters.AgeFrom, filters.AgeTo) switch
            {
                (int fromAge, int toAge) => $"Возраст: {fromAge}–{toAge}",
                (int fromAge, null) => $"Возраст: от {fromAge}",
                (null, int toAge) => $"Возраст: до {toAge}",
                _ => "Возраст"
            };
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = ageLabel,
                RemoveUrl = BuildListUrl(
                    path,
                    pageSize,
                    ListPageSizeDefaults.Responses,
                    ResponsesFilterPairs(filters, period, ("ageFrom", null), ("ageTo", null)))
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.VacancyQuery))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Вакансия: {filters.VacancyQuery}",
                RemoveUrl = BuildListUrl(
                    path,
                    pageSize,
                    ListPageSizeDefaults.Responses,
                    ResponsesFilterPairs(filters, period, ("vacancy", null)))
            });
        }

        return chips;
    }

    public static IReadOnlyList<ActiveFilterChipViewModel> ForAccounts(
        string? searchQuery,
        string? tab,
        IReadOnlyList<AccountTabViewModel> tabs,
        Guid? workerId,
        IReadOnlyList<EventFilterOptionViewModel> workers,
        int pageSize)
    {
        const string path = "/Accounts";
        var chips = new List<ActiveFilterChipViewModel>();
        var normalizedTab = string.IsNullOrWhiteSpace(tab) || tab == "all" ? null : tab;
        var pageSizeValue = pageSize == ListPageSizeDefaults.Accounts ? null : pageSize.ToString();

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Поиск: {searchQuery}",
                RemoveUrl = BuildUrl(path,
                    ("tab", normalizedTab),
                    ("workerId", workerId?.ToString()),
                    ("pageSize", pageSizeValue),
                    ("sort", null),
                    ("dir", null),
                    ("page", "1"))
            });
        }

        if (!string.IsNullOrWhiteSpace(normalizedTab))
        {
            var label = tabs.FirstOrDefault(t => t.Id == normalizedTab)?.Label ?? normalizedTab;
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = label,
                RemoveUrl = BuildUrl(path,
                    ("q", searchQuery),
                    ("workerId", workerId?.ToString()),
                    ("pageSize", pageSizeValue),
                    ("sort", null),
                    ("dir", null),
                    ("page", "1"))
            });
        }

        if (workerId is Guid wid)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Воркер: {OptionLabel(workers, wid.ToString()) ?? wid.ToString()[..8]}",
                RemoveUrl = BuildUrl(path,
                    ("q", searchQuery),
                    ("tab", normalizedTab),
                    ("pageSize", pageSizeValue),
                    ("sort", null),
                    ("dir", null),
                    ("page", "1"))
            });
        }

        return chips;
    }

    public static IReadOnlyList<ActiveFilterChipViewModel> ForWorkers(string? searchQuery, string? statusFilter, int pageSize)
    {
        const string path = "/Workers";
        var chips = new List<ActiveFilterChipViewModel>();
        var pageSizeValue = pageSize == ListPageSizeDefaults.Workers ? null : pageSize.ToString();

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Поиск: {searchQuery}",
                RemoveUrl = BuildUrl(path,
                    ("status", statusFilter),
                    ("pageSize", pageSizeValue),
                    ("page", "1"))
            });
        }

        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            var label = statusFilter switch
            {
                "online" => "Онлайн",
                "offline" => "Оффлайн",
                _ => statusFilter
            };
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Статус: {label}",
                RemoveUrl = BuildUrl(path,
                    ("q", searchQuery),
                    ("pageSize", pageSizeValue),
                    ("page", "1"))
            });
        }

        return chips;
    }

    public static IReadOnlyList<ActiveFilterChipViewModel> ForStatistics(
        StatisticsFiltersViewModel filters,
        DashboardPeriod period,
        IReadOnlyList<EventFilterOptionViewModel> workers,
        IReadOnlyList<EventFilterOptionViewModel> accounts)
    {
        const string path = "/Statistics";
        var chips = new List<ActiveFilterChipViewModel>();
        var from = period.From.ToString("yyyy-MM-dd");
        var to = period.To.ToString("yyyy-MM-dd");
        var tz = period.TimeZoneOffsetMinutes.ToString();

        foreach (var workerId in filters.WorkerIds)
        {
            var pairs = new List<(string Key, string? Value)>
            {
                ("from", from),
                ("to", to),
                ("tz", tz)
            };
            pairs.AddRange(filters.WorkerIds
                .Where(id => id != workerId)
                .Select(id => ("workerIds", (string?)id.ToString())));
            pairs.AddRange(filters.AccountIds.Select(id => ("accountIds", (string?)id.ToString())));

            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Воркер: {OptionLabel(workers, workerId.ToString()) ?? workerId.ToString()[..8]}",
                RemoveUrl = BuildUrl(path, pairs.ToArray())
            });
        }

        foreach (var accountId in filters.AccountIds)
        {
            var pairs = new List<(string Key, string? Value)>
            {
                ("from", from),
                ("to", to),
                ("tz", tz)
            };
            pairs.AddRange(filters.WorkerIds.Select(id => ("workerIds", (string?)id.ToString())));
            pairs.AddRange(filters.AccountIds
                .Where(id => id != accountId)
                .Select(id => ("accountIds", (string?)id.ToString())));

            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Аккаунт: {OptionLabel(accounts, accountId.ToString()) ?? accountId.ToString()[..8]}",
                RemoveUrl = BuildUrl(path, pairs.ToArray())
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.VacancyQuery))
        {
            var pairs = new List<(string Key, string? Value)>
            {
                ("from", from),
                ("to", to),
                ("tz", tz)
            };
            pairs.AddRange(filters.WorkerIds.Select(id => ("workerIds", (string?)id.ToString())));
            pairs.AddRange(filters.AccountIds.Select(id => ("accountIds", (string?)id.ToString())));

            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Вакансия: {filters.VacancyQuery}",
                RemoveUrl = BuildUrl(path, pairs.ToArray())
            });
        }

        return chips;
    }
}
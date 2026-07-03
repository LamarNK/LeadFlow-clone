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

    public static IReadOnlyList<ActiveFilterChipViewModel> ForEvents(
        EventsFilterViewModel filters,
        IReadOnlyList<EventFilterOptionViewModel> eventTypes,
        IReadOnlyList<EventFilterOptionViewModel> workers,
        IReadOnlyList<EventFilterOptionViewModel> accounts,
        IReadOnlyList<EventFilterOptionViewModel> levels,
        string? journalView = null)
    {
        const string path = "/Events";
        var chips = new List<ActiveFilterChipViewModel>();
        var viewPair = string.IsNullOrWhiteSpace(journalView) || journalView == "all"
            ? Array.Empty<(string, string?)>()
            : new[] { ("view", journalView) };

        if (!string.IsNullOrWhiteSpace(filters.SearchQuery))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Поиск: {filters.SearchQuery}",
                RemoveUrl = BuildUrl(path,
                    viewPair
                        .Concat([
                            ("type", filters.Type),
                            ("workerId", filters.WorkerId?.ToString()),
                            ("accountId", filters.AccountId?.ToString()),
                            ("level", filters.Level),
                            ("page", "1")
                        ]).ToArray())
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.Type))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = OptionLabel(eventTypes, filters.Type) ?? filters.Type,
                RemoveUrl = BuildUrl(path,
                    viewPair
                        .Concat([
                            ("q", filters.SearchQuery),
                            ("workerId", filters.WorkerId?.ToString()),
                            ("accountId", filters.AccountId?.ToString()),
                            ("level", filters.Level),
                            ("page", "1")
                        ]).ToArray())
            });
        }

        if (filters.WorkerId is Guid workerId)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Воркер: {OptionLabel(workers, workerId.ToString()) ?? workerId.ToString()[..8]}",
                RemoveUrl = BuildUrl(path,
                    viewPair
                        .Concat([
                            ("q", filters.SearchQuery),
                            ("type", filters.Type),
                            ("accountId", filters.AccountId?.ToString()),
                            ("level", filters.Level),
                            ("page", "1")
                        ]).ToArray())
            });
        }

        if (filters.AccountId is Guid accountId)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Аккаунт: {OptionLabel(accounts, accountId.ToString()) ?? accountId.ToString()[..8]}",
                RemoveUrl = BuildUrl(path,
                    viewPair
                        .Concat([
                            ("q", filters.SearchQuery),
                            ("type", filters.Type),
                            ("workerId", filters.WorkerId?.ToString()),
                            ("level", filters.Level),
                            ("page", "1")
                        ]).ToArray())
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.Level))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = OptionLabel(levels, filters.Level) ?? filters.Level,
                RemoveUrl = BuildUrl(path,
                    viewPair
                        .Concat([
                            ("q", filters.SearchQuery),
                            ("type", filters.Type),
                            ("workerId", filters.WorkerId?.ToString()),
                            ("accountId", filters.AccountId?.ToString()),
                            ("page", "1")
                        ]).ToArray())
            });
        }

        return chips;
    }

    public static IReadOnlyList<ActiveFilterChipViewModel> ForErrors(
        ErrorsFilterViewModel filters,
        IReadOnlyList<EventFilterOptionViewModel> severityOptions,
        IReadOnlyList<EventFilterOptionViewModel> errorTypes,
        IReadOnlyList<EventFilterOptionViewModel> workers,
        IReadOnlyList<EventFilterOptionViewModel> accounts)
    {
        const string path = "/Events";
        var chips = new List<ActiveFilterChipViewModel>();
        var viewPair = new[] { ("view", "errors") };

        if (!string.IsNullOrWhiteSpace(filters.SearchQuery))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Поиск: {filters.SearchQuery}",
                RemoveUrl = BuildUrl(path,
                    viewPair
                        .Concat([
                            ("severity", filters.Severity),
                            ("type", filters.Type),
                            ("workerId", filters.WorkerId?.ToString()),
                            ("accountId", filters.AccountId?.ToString()),
                            ("page", "1")
                        ]).ToArray())
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.Severity))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = OptionLabel(severityOptions, filters.Severity) ?? filters.Severity,
                RemoveUrl = BuildUrl(path,
                    viewPair
                        .Concat([
                            ("q", filters.SearchQuery),
                            ("type", filters.Type),
                            ("workerId", filters.WorkerId?.ToString()),
                            ("accountId", filters.AccountId?.ToString()),
                            ("page", "1")
                        ]).ToArray())
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.Type))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = OptionLabel(errorTypes, filters.Type) ?? filters.Type,
                RemoveUrl = BuildUrl(path,
                    viewPair
                        .Concat([
                            ("q", filters.SearchQuery),
                            ("severity", filters.Severity),
                            ("workerId", filters.WorkerId?.ToString()),
                            ("accountId", filters.AccountId?.ToString()),
                            ("page", "1")
                        ]).ToArray())
            });
        }

        if (filters.WorkerId is Guid workerId)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Воркер: {OptionLabel(workers, workerId.ToString()) ?? workerId.ToString()[..8]}",
                RemoveUrl = BuildUrl(path,
                    viewPair
                        .Concat([
                            ("q", filters.SearchQuery),
                            ("severity", filters.Severity),
                            ("type", filters.Type),
                            ("accountId", filters.AccountId?.ToString()),
                            ("page", "1")
                        ]).ToArray())
            });
        }

        if (filters.AccountId is Guid accountId)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Аккаунт: {OptionLabel(accounts, accountId.ToString()) ?? accountId.ToString()[..8]}",
                RemoveUrl = BuildUrl(path,
                    viewPair
                        .Concat([
                            ("q", filters.SearchQuery),
                            ("severity", filters.Severity),
                            ("type", filters.Type),
                            ("workerId", filters.WorkerId?.ToString()),
                            ("page", "1")
                        ]).ToArray())
            });
        }

        return chips;
    }

    public static IReadOnlyList<ActiveFilterChipViewModel> ForResponses(
        ResponsesFilterViewModel filters,
        DashboardPeriod period,
        IReadOnlyList<EventFilterOptionViewModel> statuses,
        IReadOnlyList<EventFilterOptionViewModel> workers,
        IReadOnlyList<EventFilterOptionViewModel> accounts)
    {
        const string path = "/Responses";
        var chips = new List<ActiveFilterChipViewModel>();
        var from = filters.DateFrom.ToString("yyyy-MM-dd");
        var to = filters.DateTo.ToString("yyyy-MM-dd");

        if (!period.IsTodayOnly)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Период: {period.Label}",
                RemoveUrl = BuildUrl(path,
                    ("from", DateTime.Today.ToString("yyyy-MM-dd")),
                    ("to", DateTime.Today.ToString("yyyy-MM-dd")),
                    ("status", filters.Status),
                    ("workerId", filters.WorkerId?.ToString()),
                    ("accountId", filters.AccountId?.ToString()),
                    ("vacancy", filters.VacancyQuery),
                    ("search", filters.SearchQuery),
                    ("page", "1"))
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.SearchQuery))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Поиск: {filters.SearchQuery}",
                RemoveUrl = BuildUrl(path,
                    ("from", from),
                    ("to", to),
                    ("status", filters.Status),
                    ("workerId", filters.WorkerId?.ToString()),
                    ("accountId", filters.AccountId?.ToString()),
                    ("vacancy", filters.VacancyQuery),
                    ("page", "1"))
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = OptionLabel(statuses, filters.Status) ?? filters.Status,
                RemoveUrl = BuildUrl(path,
                    ("from", from),
                    ("to", to),
                    ("workerId", filters.WorkerId?.ToString()),
                    ("accountId", filters.AccountId?.ToString()),
                    ("vacancy", filters.VacancyQuery),
                    ("search", filters.SearchQuery),
                    ("page", "1"))
            });
        }

        if (filters.WorkerId is Guid workerId)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Воркер: {OptionLabel(workers, workerId.ToString()) ?? workerId.ToString()[..8]}",
                RemoveUrl = BuildUrl(path,
                    ("from", from),
                    ("to", to),
                    ("status", filters.Status),
                    ("accountId", filters.AccountId?.ToString()),
                    ("vacancy", filters.VacancyQuery),
                    ("search", filters.SearchQuery),
                    ("page", "1"))
            });
        }

        if (filters.AccountId is Guid accountId)
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Аккаунт: {OptionLabel(accounts, accountId.ToString()) ?? accountId.ToString()[..8]}",
                RemoveUrl = BuildUrl(path,
                    ("from", from),
                    ("to", to),
                    ("status", filters.Status),
                    ("workerId", filters.WorkerId?.ToString()),
                    ("vacancy", filters.VacancyQuery),
                    ("search", filters.SearchQuery),
                    ("page", "1"))
            });
        }

        if (!string.IsNullOrWhiteSpace(filters.VacancyQuery))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Объявление: {filters.VacancyQuery}",
                RemoveUrl = BuildUrl(path,
                    ("from", from),
                    ("to", to),
                    ("status", filters.Status),
                    ("workerId", filters.WorkerId?.ToString()),
                    ("accountId", filters.AccountId?.ToString()),
                    ("search", filters.SearchQuery),
                    ("page", "1"))
            });
        }

        return chips;
    }

    public static IReadOnlyList<ActiveFilterChipViewModel> ForWorkers(string? searchQuery, string? statusFilter)
    {
        const string path = "/Workers";
        var chips = new List<ActiveFilterChipViewModel>();

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            chips.Add(new ActiveFilterChipViewModel
            {
                Label = $"Поиск: {searchQuery}",
                RemoveUrl = BuildUrl(path, ("status", statusFilter), ("page", "1"))
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
                RemoveUrl = BuildUrl(path, ("q", searchQuery), ("page", "1"))
            });
        }

        return chips;
    }
}
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class EventsIndexBuilder
{
    public const int DefaultPageSize = 10;

    public static readonly EventFilterOptionViewModel[] EventTypeOptions =
    [
        new() { Value = "", Label = "Все типы" },
        new() { Value = "response", Label = "Новый отклик" },
        new() { Value = "duplicate", Label = "Дубль" },
        new() { Value = "error", Label = "Ошибка" },
        new() { Value = "auth", Label = "Авторизация" },
        new() { Value = "balance", Label = "Баланс" },
        new() { Value = "start", Label = "Запуск" },
        new() { Value = "stop", Label = "Остановка" },
        new() { Value = "info", Label = "Информация" }
    ];

    public static readonly EventFilterOptionViewModel[] LevelOptions =
    [
        new() { Value = "", Label = "Все уровни" },
        new() { Value = "success", Label = "Успех" },
        new() { Value = "warning", Label = "Предупреждение" },
        new() { Value = "error", Label = "Ошибка" },
        new() { Value = "info", Label = "Информация" }
    ];

    public static EventsIndexViewModel Build(
        IReadOnlyList<EventRowViewModel> allRows,
        EventsFilterViewModel filters,
        int page,
        IReadOnlyList<EventFilterOptionViewModel>? workerOptions = null,
        IReadOnlyList<EventFilterOptionViewModel>? accountOptions = null,
        int pageSize = DefaultPageSize)
    {
        page = Math.Max(1, page);

        var filtered = FilterRows(allRows, filters);
        var total = filtered.Count;
        var paged = filtered
            .OrderByDescending(e => e.OccurredAtLocal)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var summary = Summarize(allRows);

        return new EventsIndexViewModel
        {
            Filters = filters,
            EventTypes = EventTypeOptions,
            Workers = workerOptions ?? BuildWorkerOptions(allRows),
            Accounts = accountOptions ?? BuildAccountOptions(allRows),
            Levels = LevelOptions,
            KpiCards = BuildKpiCards(summary),
            Events = paged,
            Pagination = new PaginationViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalItems = total
            }
        };
    }

    public static EventRowViewModel MapEvent(WorkerEventListItem item, string? accountName = null)
    {
        var level = NormalizeLevel(item.Level);
        var type = InferEventType(item.Message, item.Details, level);
        var (typeLabel, typeIcon, typeTone) = EventTypePresentation(type);
        var description = BuildDescription(item.Message, item.Details);

        return new EventRowViewModel
        {
            Id = item.Id,
            OccurredAtLocal = item.CreatedAtUtc.ToLocalTime(),
            EventType = type,
            EventTypeLabel = typeLabel,
            EventTypeIcon = typeIcon,
            EventTypeTone = typeTone,
            Level = level,
            LevelLabel = LevelLabel(level),
            AccountName = accountName,
            AccountId = item.AccountId,
            WorkerId = item.WorkerId,
            WorkerName = FormatWorkerName(item.WorkerDisplayName),
            Description = description,
            CopyText = $"{item.Message}{(string.IsNullOrWhiteSpace(item.Details) ? "" : " — " + item.Details)}"
        };
    }

    private static IReadOnlyList<EventRowViewModel> FilterRows(
        IReadOnlyList<EventRowViewModel> rows,
        EventsFilterViewModel filters)
    {
        IEnumerable<EventRowViewModel> query = rows;

        if (!string.IsNullOrWhiteSpace(filters.SearchQuery))
        {
            var q = filters.SearchQuery.Trim();
            query = query.Where(e =>
                e.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.EventTypeLabel.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (e.AccountName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || e.WorkerName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filters.Type))
            query = query.Where(e => e.EventType == filters.Type);

        if (filters.WorkerId.HasValue)
            query = query.Where(e => e.WorkerId == filters.WorkerId.Value);

        if (!string.IsNullOrWhiteSpace(filters.Account))
            query = query.Where(e => e.AccountName == filters.Account);

        if (!string.IsNullOrWhiteSpace(filters.Level))
        {
            query = filters.Level switch
            {
                "errors" => query.Where(e => e.Level is "error" or "warning"),
                _ => query.Where(e => e.Level == filters.Level)
            };
        }

        return query.ToList();
    }

    private static EventsSummaryViewModel Summarize(IReadOnlyList<EventRowViewModel> rows) => new()
    {
        Total = rows.Count,
        Success = rows.Count(e => e.Level == "success"),
        Warning = rows.Count(e => e.Level == "warning"),
        Error = rows.Count(e => e.Level == "error"),
        Info = rows.Count(e => e.Level == "info")
    };

    private static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(EventsSummaryViewModel summary)
    {
        var total = Math.Max(1, summary.Total);
        string Pct(int value) => $"{value * 100.0 / total:0.#}%";

        return
        [
            new()
            {
                Label = "Всего событий",
                Value = summary.Total.ToString(),
                CountValue = summary.Total,
                Delta = "За период",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-clipboard",
                IconTone = "blue"
            },
            new()
            {
                Label = "Успешных",
                Value = summary.Success.ToString(),
                CountValue = summary.Success,
                Delta = Pct(summary.Success),
                DeltaTone = "good",
                IconClass = "fa-regular fa-circle-check",
                IconTone = "green"
            },
            new()
            {
                Label = "Предупреждений",
                Value = summary.Warning.ToString(),
                CountValue = summary.Warning,
                Delta = Pct(summary.Warning),
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-triangle-exclamation",
                IconTone = "orange"
            },
            new()
            {
                Label = "Ошибок",
                Value = summary.Error.ToString(),
                CountValue = summary.Error,
                Delta = Pct(summary.Error),
                DeltaTone = "bad",
                IconClass = "fa-regular fa-circle-xmark",
                IconTone = "orange"
            },
            new()
            {
                Label = "Информационных",
                Value = summary.Info.ToString(),
                CountValue = summary.Info,
                Delta = Pct(summary.Info),
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-circle-info",
                IconTone = "blue"
            }
        ];
    }

    private static IReadOnlyList<EventFilterOptionViewModel> BuildWorkerOptions(IReadOnlyList<EventRowViewModel> rows)
    {
        var options = new List<EventFilterOptionViewModel> { new() { Value = "", Label = "Все воркеры" } };
        options.AddRange(rows
            .GroupBy(e => e.WorkerId)
            .Select(g => g.First())
            .OrderBy(e => e.WorkerName)
            .Select(e => new EventFilterOptionViewModel
            {
                Value = e.WorkerId.ToString(),
                Label = e.WorkerName
            }));
        return options;
    }

    private static IReadOnlyList<EventFilterOptionViewModel> BuildAccountOptions(IReadOnlyList<EventRowViewModel> rows)
    {
        var options = new List<EventFilterOptionViewModel> { new() { Value = "", Label = "Все аккаунты" } };
        options.AddRange(rows
            .Where(e => !string.IsNullOrWhiteSpace(e.AccountName))
            .Select(e => e.AccountName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .Select(a => new EventFilterOptionViewModel { Value = a, Label = a }));
        return options;
    }

    private static string NormalizeLevel(string level) => level.ToLowerInvariant() switch
    {
        "error" => "error",
        "warning" => "warning",
        "info" => "info",
        _ => "success"
    };

    private static string LevelLabel(string level) => level switch
    {
        "error" => "Ошибка",
        "warning" => "Предупреждение",
        "info" => "Информация",
        _ => "Успех"
    };

    private static string InferEventType(string message, string? details, string level)
    {
        var text = $"{message} {details}".ToLowerInvariant();
        if (text.Contains("дубл") || text.Contains("duplicate"))
            return "duplicate";
        if (text.Contains("авториз") || text.Contains("вход"))
            return "auth";
        if (text.Contains("баланс"))
            return "balance";
        if (text.Contains("запуск") || text.Contains("старт") || text.Contains("running"))
            return "start";
        if (text.Contains("останов") || text.Contains("heartbeat") || text.Contains("не получен"))
            return "stop";
        if (level == "error" || text.Contains("ошиб"))
            return "error";
        if (text.Contains("отклик"))
            return "response";
        return "info";
    }

    public static (string Label, string Icon, string Tone) EventTypePresentation(string type) => type switch
    {
        "response" => ("Новый отклик", "fa-regular fa-circle-check", "success"),
        "duplicate" => ("Дубликат", "fa-solid fa-triangle-exclamation", "warning"),
        "error" => ("Ошибка отправки", "fa-regular fa-circle-xmark", "error"),
        "auth" => ("Авторизация", "fa-solid fa-key", "info"),
        "balance" => ("Обновление баланса", "fa-solid fa-circle-info", "info"),
        "start" => ("Запуск", "fa-solid fa-play", "success"),
        "stop" => ("Остановка", "fa-solid fa-stop", "warning"),
        _ => ("Информация", "fa-solid fa-circle-info", "info")
    };

    private static string BuildDescription(string message, string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return message;

        if (message.Contains(details, StringComparison.OrdinalIgnoreCase))
            return message;

        return $"{message} — {details}";
    }

    private static string FormatWorkerName(string workerDisplayName)
    {
        if (workerDisplayName.StartsWith("VDS-", StringComparison.OrdinalIgnoreCase))
        {
            var hash = Math.Abs(workerDisplayName.GetHashCode());
            return $"Worker #{(hash % 12) + 1}";
        }

        return workerDisplayName;
    }
}
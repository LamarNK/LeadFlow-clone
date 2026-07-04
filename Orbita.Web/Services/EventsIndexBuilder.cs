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
        new() { Value = "captcha", Label = "Капча / блок IP" },
        new() { Value = "switch", Label = "Не переключился" },
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
        int pageSize = DefaultPageSize,
        string? journalView = null,
        string? sort = null,
        string? sortDir = null,
        IOfficeContext? officeContext = null)
    {
        page = Math.Max(1, page);
        var tableSort = TableSort.Parse(sort, sortDir, TableSort.Events.Default, TableSort.Events.Columns);

        var filtered = FilterRows(allRows, filters);
        var sorted = TableSort.Events.Apply(filtered, tableSort).ToList();
        var total = sorted.Count;
        var paged = sorted
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var summary = Summarize(allRows);
        var workers = workerOptions ?? BuildWorkerOptions(allRows);
        var accounts = accountOptions ?? BuildAccountOptions(allRows);
        var activeFilterChips = FilterChipsBuilder.ForEvents(filters, EventTypeOptions, workers, accounts, LevelOptions, journalView);

        return new EventsIndexViewModel
        {
            Header = officeContext is null
                ? PageHeaderBuilder.EventsList()
                : PageHeaderBuilder.WithOfficeScope(PageHeaderBuilder.EventsList(), officeContext),
            JournalView = journalView ?? "all",
            Filters = filters,
            EventTypes = EventTypeOptions,
            Workers = workers,
            Accounts = accounts,
            Levels = LevelOptions,
            KpiCards = BuildKpiCards(summary),
            Events = paged,
            Pagination = new PaginationViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalItems = total
            },
            HasActiveFilters = HasActiveFilters(filters),
            ActiveFilterChips = activeFilterChips,
            Sort = tableSort
        };
    }

    public static bool HasActiveFilters(EventsFilterViewModel filters) =>
        !string.IsNullOrWhiteSpace(filters.Type)
        || filters.WorkerId.HasValue
        || filters.AccountId.HasValue
        || !string.IsNullOrWhiteSpace(filters.Level)
        || !string.IsNullOrWhiteSpace(filters.SearchQuery);

    public static EventRowViewModel MapEvent(WorkerEventListItem item, string? accountName = null)
    {
        var level = NormalizeLevel(item.Level);
        var type = InferEventType(item.Message, item.Details, level);
        var (typeLabel, typeIcon, typeTone) = EventTypePresentation(type, item.Message);
        var description = BuildDescription(item.Message, item.Details);

        return new EventRowViewModel
        {
            Id = item.Id,
            OccurredAtUtc = item.CreatedAtUtc,
            EventType = type,
            EventTypeLabel = typeLabel,
            EventTypeIcon = typeIcon,
            EventTypeTone = typeTone,
            Level = level,
            LevelLabel = LevelLabel(level),
            AccountName = accountName ?? item.AccountDisplayName,
            AccountId = item.AccountId,
            WorkerId = item.WorkerId,
            WorkerName = FormatWorkerName(item.WorkerDisplayName),
            Description = description,
            CopyText = description,
            AttachmentId = WorkerEventDetailsParser.TryParseAttachmentId(item.Details)
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

        if (filters.AccountId.HasValue)
            query = query.Where(e => e.AccountId == filters.AccountId.Value);

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
                Key = "total",
                Href = KpiCardLinks.EventsCard("total"),
                Label = "Всего событий",
                Value = summary.Total.ToString(),
                CountValue = summary.Total,
                Delta = "Недавние",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-clipboard",
                IconTone = "blue"
            },
            new()
            {
                Key = "success",
                Href = KpiCardLinks.EventsCard("success"),
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
                Key = "warning",
                Href = KpiCardLinks.EventsCard("warning"),
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
                Key = "error",
                Href = KpiCardLinks.EventsCard("error"),
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
            .Where(e => e.AccountId.HasValue && !string.IsNullOrWhiteSpace(e.AccountName))
            .GroupBy(e => e.AccountId!.Value)
            .Select(g => g.First())
            .OrderBy(e => e.AccountName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new EventFilterOptionViewModel
            {
                Value = e.AccountId!.Value.ToString(),
                Label = e.AccountName!
            }));
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
        if (WorkerEventClassifier.MapIssueLabelToEventType(message) is { } issueType)
            return issueType;
        if (WorkerEventClassifier.IsCaptcha(text, details))
            return "captcha";
        if (text.Contains("авториз") || text.Contains("вход") || text.Contains("нужен вход") || text.Contains("требуется действие"))
            return "auth";
        if (text.Contains("баланс"))
            return "balance";
        if (text.Contains("запуск") || text.Contains("старт") || text.Contains("running"))
            return "start";
        if (text.Contains("останов") || text.Contains("heartbeat") || text.Contains("не получен"))
            return "stop";
        if (text.Contains("лимит adspower") || text.Contains("rate limit adspower"))
            return "error";
        if (text.Contains("субпрофили ") && text.Contains("парсер"))
            return "error";
        if (WorkerEventClassifier.IsAutomationFailure(text, level))
            return "error";
        if (level == "error" || text.Contains("ошиб"))
            return "error";
        if (WorkerEventClassifier.IsNewResponseCandidate(text, level))
            return "response";
        return "info";
    }

    public static (string Label, string Icon, string Tone) EventTypePresentation(string type, string? message = null)
    {
        if (type == "error" && message is not null)
        {
            var issueLabel = WorkerEventClassifier.TryParseIssueLabel(message)?.ToLowerInvariant();
            if (issueLabel is not null)
            {
                return issueLabel switch
                {
                    "ошибка парсинга" => ("Ошибка парсинга", "fa-regular fa-circle-xmark", "error"),
                    "таймаут" => ("Таймаут", "fa-regular fa-circle-xmark", "error"),
                    "лимит частоты adspower" or "дневной лимит adspower" => ("Лимит AdsPower", "fa-regular fa-circle-xmark", "error"),
                    "профиль занят" => ("Профиль AdsPower занят", "fa-regular fa-circle-xmark", "error"),
                    "проблема" => ("Сбой автоматизации", "fa-regular fa-circle-xmark", "error"),
                    _ => ("Сбой автоматизации", "fa-regular fa-circle-xmark", "error")
                };
            }

            var lower = message.ToLowerInvariant();
            if (lower.Contains("лимит adspower") || lower.Contains("rate limit adspower"))
                return ("Лимит AdsPower", "fa-regular fa-circle-xmark", "error");
            if (AdsPowerErrorMessageNormalizer.LooksLikeProfileInUse(message)
                || lower.Contains("профиль занят"))
                return ("Профиль AdsPower занят", "fa-regular fa-circle-xmark", "error");
            if (lower.Contains("субпрофили ") || lower.Contains("не удалось") || lower.Contains("неизвестная страница"))
                return ("Сбой автоматизации", "fa-regular fa-circle-xmark", "error");
        }

        return type switch
        {
        "response" => ("Новый отклик", "fa-regular fa-circle-check", "success"),
        "duplicate" => ("Дубликат", "fa-solid fa-triangle-exclamation", "warning"),
        "captcha" => ("Капча / блок IP", "fa-solid fa-shield-halved", "warning"),
        "switch" => ("Не переключился", "fa-solid fa-arrows-rotate", "warning"),
        "error" => ("Ошибка отправки", "fa-regular fa-circle-xmark", "error"),
        "auth" => ("Авторизация", "fa-solid fa-key", "info"),
        "balance" => ("Обновление баланса", "fa-solid fa-circle-info", "info"),
        "start" => ("Запуск", "fa-solid fa-play", "success"),
        "stop" => ("Остановка", "fa-solid fa-stop", "warning"),
        _ => ("Информация", "fa-solid fa-circle-info", "info")
        };
    }

    private static string BuildDescription(string message, string? details) =>
        WorkerEventDetailsParser.FormatForDisplay(message, details);

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
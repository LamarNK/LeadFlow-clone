using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class ErrorsIndexBuilder
{
    public const int DefaultPageSize = 10;

    public static readonly EventFilterOptionViewModel[] SeverityOptions =
    [
        new() { Value = "", Label = "Все уровни" },
        new() { Value = "critical", Label = "Критический" },
        new() { Value = "high", Label = "Высокий" },
        new() { Value = "medium", Label = "Средний" },
        new() { Value = "low", Label = "Низкий" }
    ];

    public static readonly EventFilterOptionViewModel[] ErrorTypeOptions =
    [
        new() { Value = "", Label = "Все типы" },
        new() { Value = "auth", Label = "Авторизация" },
        new() { Value = "bitrix", Label = "Отправка в Bitrix24" },
        new() { Value = "network", Label = "Сеть" },
        new() { Value = "balance", Label = "Баланс" },
        new() { Value = "blocked", Label = "Блокировка аккаунта" },
        new() { Value = "automation", Label = "Сбой автоматизации" },
        new() { Value = "parsing", Label = "Парсинг" },
        new() { Value = "postgres", Label = "PostgreSQL" },
        new() { Value = "api", Label = "API" },
        new() { Value = "unknown", Label = "Неизвестная" }
    ];

    public static ErrorsIndexViewModel Build(
        IReadOnlyList<ErrorRowViewModel> allRows,
        ErrorsFilterViewModel filters,
        int page,
        int pageSize = DefaultPageSize)
    {
        page = Math.Max(1, page);
        var filtered = FilterRows(allRows, filters);
        var total = filtered.Count;
        var paged = filtered
            .OrderByDescending(e => e.LastSeenUtc)
            .ThenByDescending(e => e.OccurrenceCount)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var summary = Summarize(allRows);
        var workers = BuildWorkerOptions(allRows);
        var accounts = BuildAccountOptions(allRows);
        var activeFilterChips = FilterChipsBuilder.ForErrors(filters, SeverityOptions, ErrorTypeOptions, workers, accounts);

        return new ErrorsIndexViewModel
        {
            Header = PageHeaderBuilder.ErrorsList(),
            Filters = filters,
            SeverityOptions = SeverityOptions,
            ErrorTypes = ErrorTypeOptions,
            Workers = workers,
            Accounts = accounts,
            KpiCards = BuildKpiCards(summary),
            Errors = paged,
            Pagination = new PaginationViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalItems = total
            },
            HasActiveFilters = HasActiveFilters(filters),
            ActiveFilterChips = activeFilterChips
        };
    }

    public static bool HasActiveFilters(ErrorsFilterViewModel filters) =>
        !string.IsNullOrWhiteSpace(filters.Severity)
        || !string.IsNullOrWhiteSpace(filters.Type)
        || filters.WorkerId.HasValue
        || filters.AccountId.HasValue
        || !string.IsNullOrWhiteSpace(filters.SearchQuery);

    public static ErrorRowViewModel MapEvent(WorkerEventListItem item, string? accountName = null)
    {
        var text = $"{item.Message} {item.Details}";
        var errorType = InferErrorType(item.Message, item.Details);
        var severity = InferSeverity(item.Level, text);
        var occurredAt = item.CreatedAtUtc;

        return new ErrorRowViewModel
        {
            Id = item.Id,
            OccurredAtUtc = occurredAt,
            Severity = severity,
            SeverityLabel = SeverityLabel(severity),
            ErrorType = errorType,
            ErrorTypeLabel = ErrorTypeLabel(errorType),
            Message = BuildMessage(item.Message, item.Details),
            CopyText = BuildMessage(item.Message, item.Details),
            AccountName = accountName ?? item.AccountDisplayName,
            AccountId = item.AccountId,
            WorkerId = item.WorkerId,
            WorkerName = FormatWorkerName(item.WorkerDisplayName),
            OccurrenceCount = 1,
            LastSeenUtc = occurredAt,
            AttachmentId = WorkerEventDetailsParser.TryParseAttachmentId(item.Details)
        };
    }

    private static IReadOnlyList<ErrorRowViewModel> FilterRows(
        IReadOnlyList<ErrorRowViewModel> rows,
        ErrorsFilterViewModel filters)
    {
        IEnumerable<ErrorRowViewModel> query = rows;

        if (!string.IsNullOrWhiteSpace(filters.SearchQuery))
        {
            var q = filters.SearchQuery.Trim();
            query = query.Where(e =>
                e.Message.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.ErrorTypeLabel.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (e.AccountName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || e.WorkerName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filters.Severity))
            query = query.Where(e => e.Severity == filters.Severity);

        if (!string.IsNullOrWhiteSpace(filters.Type))
            query = query.Where(e => e.ErrorType == filters.Type);

        if (filters.WorkerId.HasValue)
            query = query.Where(e => e.WorkerId == filters.WorkerId.Value);

        if (filters.AccountId.HasValue)
            query = query.Where(e => e.AccountId == filters.AccountId.Value);

        return query.ToList();
    }

    private static ErrorsSummaryViewModel Summarize(IReadOnlyList<ErrorRowViewModel> rows) => new()
    {
        Total = rows.Count,
        Critical = rows.Count(e => e.Severity == "critical"),
        High = rows.Count(e => e.Severity == "high"),
        Medium = rows.Count(e => e.Severity == "medium"),
        Low = rows.Count(e => e.Severity == "low")
    };

    private static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(ErrorsSummaryViewModel summary)
    {
        var total = Math.Max(1, summary.Total);
        string Pct(int value) => $"{value * 100.0 / total:0.#}%";

        return
        [
            new()
            {
                Key = "total",
                Href = KpiCardLinks.ErrorsCard("total"),
                Label = "Всего ошибок",
                Value = summary.Total.ToString(),
                CountValue = summary.Total,
                Delta = "Недавние",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-circle-xmark",
                IconTone = "orange"
            },
            new()
            {
                Key = "critical",
                Href = KpiCardLinks.ErrorsCard("critical"),
                Label = "Критические",
                Value = summary.Critical.ToString(),
                CountValue = summary.Critical,
                Delta = Pct(summary.Critical),
                DeltaTone = "bad",
                IconClass = "fa-solid fa-bolt",
                IconTone = "orange"
            },
            new()
            {
                Key = "high",
                Href = KpiCardLinks.ErrorsCard("high"),
                Label = "Высокий уровень",
                Value = summary.High.ToString(),
                CountValue = summary.High,
                Delta = Pct(summary.High),
                DeltaTone = "bad",
                IconClass = "fa-solid fa-triangle-exclamation",
                IconTone = "orange"
            },
            new()
            {
                Key = "medium",
                Href = KpiCardLinks.ErrorsCard("medium"),
                Label = "Средний уровень",
                Value = summary.Medium.ToString(),
                CountValue = summary.Medium,
                Delta = Pct(summary.Medium),
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-circle-exclamation",
                IconTone = "blue"
            },
            new()
            {
                Key = "low",
                Href = KpiCardLinks.ErrorsCard("low"),
                Label = "Низкий уровень",
                Value = summary.Low.ToString(),
                CountValue = summary.Low,
                Delta = Pct(summary.Low),
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-circle-info",
                IconTone = "blue"
            }
        ];
    }

    private static IReadOnlyList<EventFilterOptionViewModel> BuildWorkerOptions(IReadOnlyList<ErrorRowViewModel> rows)
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

    private static IReadOnlyList<EventFilterOptionViewModel> BuildAccountOptions(IReadOnlyList<ErrorRowViewModel> rows)
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

    public static string InferErrorType(string message, string? details = null)
    {
        var text = $"{message} {details}";
        if (WorkerEventClassifier.MapIssueLabelToErrorType(message) is { } issueType)
            return issueType;
        if (WorkerEventClassifier.IsCaptcha(text, details))
            return "blocked";
        if (text.Contains("авториз", StringComparison.OrdinalIgnoreCase)
            || text.Contains("логин", StringComparison.OrdinalIgnoreCase)
            || text.Contains("парол", StringComparison.OrdinalIgnoreCase)
            || text.Contains("нужен вход", StringComparison.OrdinalIgnoreCase))
            return "auth";
        if (text.Contains("bitrix", StringComparison.OrdinalIgnoreCase)
            || text.Contains("crm", StringComparison.OrdinalIgnoreCase))
            return "bitrix";
        if (text.Contains("postgres", StringComparison.OrdinalIgnoreCase)
            || text.Contains("база данных", StringComparison.OrdinalIgnoreCase))
            return "postgres";
        if (WorkerEventClassifier.IsNetworkFailure(text))
            return "network";
        if (text.Contains("баланс", StringComparison.OrdinalIgnoreCase))
            return "balance";
        if (text.Contains("парс", StringComparison.OrdinalIgnoreCase)
            || text.Contains("parse", StringComparison.OrdinalIgnoreCase))
            return "parsing";
        if (text.Contains("лимит adspower", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate limit adspower", StringComparison.OrdinalIgnoreCase)
            || text.Contains("api", StringComparison.OrdinalIgnoreCase)
            || text.Contains("adspower", StringComparison.OrdinalIgnoreCase))
            return "api";
        if (WorkerEventClassifier.IsAutomationFailure(text, "Warning"))
            return "automation";
        return "unknown";
    }

    public static string ErrorTypeLabel(string type) => type switch
    {
        "auth" => "Ошибка авторизации",
        "bitrix" => "Ошибка отправки",
        "network" => "Ошибка сети",
        "balance" => "Ошибка баланса",
        "blocked" => "Блокировка аккаунта",
        "automation" => "Сбой автоматизации",
        "parsing" => "Ошибка парсинга",
        "postgres" => "Ошибка PostgreSQL",
        "api" => "Ошибка API",
        _ => "Неизвестная ошибка"
    };

    public static string InferSeverity(string level, string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("postgres") || lower.Contains("критич") || lower.Contains("недоступ"))
            return "critical";
        if (level.Equals("Error", StringComparison.OrdinalIgnoreCase)
            && (lower.Contains("bitrix") || lower.Contains("авториз") || lower.Contains("блок")))
            return "high";
        if (level.Equals("Error", StringComparison.OrdinalIgnoreCase))
            return "medium";
        if (level.Equals("Warning", StringComparison.OrdinalIgnoreCase))
            return "medium";
        return "low";
    }

    public static string SeverityLabel(string severity) => severity switch
    {
        "critical" => "Критический",
        "high" => "Высокий",
        "medium" => "Средний",
        _ => "Низкий"
    };

    private static string BuildMessage(string message, string? details) =>
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
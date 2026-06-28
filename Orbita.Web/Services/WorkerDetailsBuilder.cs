using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class WorkerDetailsBuilder
{
    public static WorkerDetailsViewModel Build(
        WorkerDetail worker,
        IReadOnlyList<WorkerAccountRowViewModel> accounts,
        IReadOnlyList<DashboardEventRowViewModel> events,
        WorkerExtraInfoViewModel? extra = null,
        WorkerRowViewModel? summary = null)
    {
        extra ??= new WorkerExtraInfoViewModel();
        var stats = worker.LatestStats;
        var lastActivity = worker.LastSeenAtUtc
            ?? summary?.LastActivityUtc;
        var activeAccounts = accounts.Count(a => a.StatusTone == "success");
        var totalAccounts = accounts.Count > 0
            ? accounts.Count
            : summary?.TotalAccounts ?? stats?.ConnectedAccounts ?? 0;
        if (totalAccounts == 0 && summary is not null)
            totalAccounts = summary.TotalAccounts;

        if (activeAccounts == 0 && summary is not null)
            activeAccounts = summary.ActiveAccounts;

        var responses = summary?.Responses ?? stats?.TotalToday ?? 0;
        var duplicates = summary?.Duplicates ?? stats?.Duplicates ?? 0;
        var errors = summary?.Errors ?? stats?.Errors ?? 0;
        var uptime = FormatUptime(extra.StartedAtUtc);
        var activePct = totalAccounts == 0
            ? 0
            : activeAccounts * 100 / totalAccounts;

        var hourly = stats?.HourlyActivity.Count > 0
            ? DashboardChartsBuilder.FromHourlyActivity(stats.HourlyActivity)
            : HourlyResponsesGenerator.BuildEmptyDailyPoints();

        return new WorkerDetailsViewModel
        {
            Header = PageHeaderBuilder.WorkerDetails(worker.DisplayName, DateTime.UtcNow),
            WorkerId = worker.Id,
            Breadcrumbs =
            [
                new() { Label = "Воркеры", Url = "/Workers" },
                new() { Label = worker.DisplayName, IsActive = true }
            ],
            DisplayName = worker.DisplayName,
            IsOnline = worker.IsOnline,
            LastActivityUtc = lastActivity,
            UpdatedAtUtc = DateTime.UtcNow,
            KpiCards = BuildKpiCards(activeAccounts, totalAccounts, activePct, responses, duplicates, errors, uptime),
            InfoItems = BuildInfoItems(worker, extra, lastActivity, uptime),
            ActivityChart = new LineChartViewModel
            {
                Labels = hourly.Select(p => p.Label).ToList(),
                Values = hourly.Select(p => p.Value).ToList()
            },
            Events = events,
            PeriodStats = BuildPeriodStats(stats, responses, duplicates, errors),
            Accounts = accounts,
            MaxConcurrentAccounts = worker.MaxConcurrentAccounts,
            AdsPowerApiBaseUrl = worker.AdsPowerApiBaseUrl,
            AdsPowerApiKey = worker.AdsPowerApiKey,
            CpuPercent = worker.LastCpuPercent,
            RamPercent = worker.LastRamPercent,
            RamUsedMb = worker.LastRamUsedMb,
            RamTotalMb = worker.LastRamTotalMb
        };
    }

    public static WorkerAccountRowViewModel MapAccount(
        WorkerAccountDto account,
        WorkerBalanceDto? balance,
        int responses = 0,
        int errors = 0)
    {
        var (label, tone) = MapAccountStatus(account.Status, account.IsEnabled);
        return new WorkerAccountRowViewModel
        {
            Id = account.AccountId,
            DisplayName = account.DisplayName,
            IsEnabledInPanel = account.IsEnabledInPanel,
            AdsPowerProfileId = account.AdsPowerProfileId,
            StatusLabel = label,
            StatusTone = tone,
            BalanceText = balance is null ? "—" : $"{balance.TotalBalance:N0} ₽",
            Responses = responses,
            LastActivityUtc = account.LastMonitoringAt,
            Errors = errors
        };
    }

    private static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(
        int activeAccounts,
        int totalAccounts,
        int activePct,
        int responses,
        int duplicates,
        int errors,
        string uptime) =>
    [
        new()
        {
            Label = "Аккаунтов",
            Value = $"{activeAccounts} / {totalAccounts}",
            CountValue = activeAccounts,
            ValueSuffix = totalAccounts > 0 ? $" / {totalAccounts}" : null,
            Delta = $"{activePct}% активны",
            DeltaTone = activePct >= 80 ? "good" : activePct >= 50 ? "neutral" : "bad",
            IconClass = "fa-regular fa-user",
            IconTone = "purple"
        },
        new()
        {
            Label = "Откликов",
            Value = responses.ToString(),
            CountValue = responses,
            Delta = "Сегодня",
            DeltaTone = "neutral",
            IconClass = "fa-regular fa-comments",
            IconTone = "blue"
        },
        new()
        {
            Label = "Дублей",
            Value = duplicates.ToString(),
            CountValue = duplicates,
            Delta = "Сегодня",
            DeltaTone = "neutral",
            IconClass = "fa-regular fa-clone",
            IconTone = "green"
        },
        new()
        {
            Label = "Ошибок",
            Value = errors.ToString(),
            CountValue = errors,
            Delta = "Сегодня",
            DeltaTone = errors > 0 ? "bad" : "good",
            IconClass = "fa-solid fa-triangle-exclamation",
            IconTone = "orange"
        },
        new()
        {
            Label = "Время работы",
            Value = uptime,
            CountValue = 0,
            Delta = "С момента запуска",
            DeltaTone = "neutral",
            IconClass = "fa-solid fa-clock",
            IconTone = "blue"
        }
    ];

    private static IReadOnlyList<WorkerInfoItemViewModel> BuildInfoItems(
        WorkerDetail worker,
        WorkerExtraInfoViewModel extra,
        DateTime? lastActivity,
        string uptime) =>
    [
        new() { Label = "Статус", Value = worker.IsOnline ? "Онлайн" : "Оффлайн" },
        new() { Label = "ID воркера", Value = worker.Id.ToString() },
        new() { Label = "Имя сервера", Value = worker.MachineName },
        new() { Label = "IP-адрес", Value = extra.IpAddress },
        new()
        {
            Label = "Дата запуска",
            TimeValue = new UtcTimeDisplayModel(extra.StartedAtUtc, "datetime")
        },
        new() { Label = "Время работы", Value = uptime },
        new() { Label = "Версия LeadFlow", Value = extra.LeadFlowVersion },
        new() { Label = "Версия агента", Value = extra.AgentVersion },
        new() { Label = "Операционная система", Value = extra.OperatingSystem },
        new()
        {
            Label = "Последняя активность",
            TimeValue = new UtcTimeDisplayModel(lastActivity, "time")
        },
        new() { Label = "Проверка соединения", Value = extra.ConnectionCheck }
    ];

    private static IReadOnlyList<WorkerPeriodStatViewModel> BuildPeriodStats(
        DashboardStatsDto? stats,
        int responses,
        int duplicates,
        int errors)
    {
        var unique = Math.Max(0, responses - duplicates);
        return
        [
            new() { Label = "Всего откликов", Value = responses.ToString() },
            new() { Label = "Уникальных откликов", Value = unique.ToString() },
            new() { Label = "Дублей", Value = duplicates.ToString() },
            new() { Label = "Ошибок", Value = errors.ToString() }
        ];
    }

    private static (string Label, string Tone) MapAccountStatus(string status, bool isEnabled)
    {
        if (!isEnabled)
            return ("Заблокирован", "blocked");

        return status switch
        {
            "Active" => ("Активен", "success"),
            "Blocked" => ("Заблокирован", "blocked"),
            "Error" or "RequiresLogin" or "RequiresManualAction" => ("Ошибка", "error"),
            _ => isEnabled ? ("Активен", "success") : ("Заблокирован", "blocked")
        };
    }

    private static string FormatUptime(DateTime? startedAtUtc)
    {
        if (startedAtUtc is null)
            return "—";

        var span = DateTime.UtcNow - startedAtUtc.Value;
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}д {span.Hours}ч";

        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}ч {span.Minutes}м";

        return $"{Math.Max(1, (int)span.TotalMinutes)}м";
    }
}
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
        WorkerRowViewModel? summary = null,
        WorkerLogsPanelViewModel? logs = null)
    {
        extra ??= new WorkerExtraInfoViewModel();
        var stats = worker.LatestStats;
        var lastActivity = worker.LastSeenAtUtc
            ?? summary?.LastActivityUtc;
        var activeAccounts = worker.ActiveAccountCount > 0 || worker.TotalAccountCount > 0
            ? worker.ActiveAccountCount
            : summary?.ActiveAccounts ?? accounts.Count(a => a.StatusTone == "success");
        var totalAccounts = worker.TotalAccountCount > 0
            ? worker.TotalAccountCount
            : accounts.Count > 0
                ? accounts.Count
                : summary?.TotalAccounts ?? stats?.ConnectedAccounts ?? 0;

        var hasDbStats = worker.TotalAccountCount > 0;
        var responses = hasDbStats
            ? worker.TodayResponses
            : summary?.Responses ?? stats?.TotalToday ?? 0;
        var duplicates = hasDbStats
            ? worker.TodayDuplicates
            : summary?.Duplicates ?? stats?.Duplicates ?? 0;
        var errors = hasDbStats
            ? worker.TodayErrors
            : summary?.Errors ?? 0;
        var uptime = FormatUptime(extra.StartedAtUtc);
        var activePct = totalAccounts == 0
            ? 0
            : activeAccounts * 100 / totalAccounts;

        var hourly = stats?.HourlyActivity.Count > 0
            ? DashboardChartsBuilder.FromHourlyActivity(stats.HourlyActivity)
            : HourlyResponsesGenerator.BuildEmptyDailyPoints();

        return new WorkerDetailsViewModel
        {
            Header = PageHeaderBuilder.WorkerDetails(worker.DisplayName, worker.MachineName, DateTime.UtcNow),
            WorkerId = worker.Id,
            Breadcrumbs =
            [
                new() { Label = "Воркеры", Url = "/Workers" },
                new() { Label = worker.DisplayName, IsActive = true }
            ],
            DisplayName = worker.DisplayName,
            MachineName = worker.MachineName,
            IsOnline = worker.IsOnline,
            IsEnabled = worker.IsEnabled,
            LastActivityUtc = lastActivity,
            UpdatedAtUtc = DateTime.UtcNow,
            KpiCards = BuildKpiCards(worker.Id, activeAccounts, totalAccounts, activePct, responses, duplicates, errors, uptime),
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
            RamTotalMb = worker.LastRamTotalMb,
            Logs = logs
        };
    }

    public static WorkerAccountRowViewModel MapAccount(
        WorkerAccountDto account,
        WorkerBalanceDto? balance)
    {
        var (label, tone) = AccountStatusMapper.ForWorkerDetails(account.Status, account.IsEnabledInPanel);
        var responses = account.TodayResponses;
        var errors = account.TodayEventErrors;
        var subProfiles = SubProfileViewModelMapper.Map(account.SubProfiles);
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
            Errors = errors > 0 ? errors : !string.IsNullOrWhiteSpace(account.LastErrorMessage) ? 1 : 0,
            // TodayEventErrors from API; LastErrorMessage is legacy fallback
            LastErrorMessage = account.LastErrorMessage,
            SubProfiles = subProfiles,
            SubProfilesSummary = SubProfileViewModelMapper.BuildSummary(subProfiles),
            CanRefreshSubProfiles = !string.IsNullOrWhiteSpace(account.AdsPowerProfileId),
            IsSubProfilesRefreshPending = SubProfileViewModelMapper.IsRefreshPending(
                account.SubProfilesRefreshRequestedAtUtc,
                account.SubProfilesRefreshedAtUtc)
        };
    }

    private static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(
        Guid workerId,
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
            Key = "accounts",
            Href = KpiCardLinks.WorkerDetailsCard("accounts", workerId),
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
            Key = "responses",
            Href = KpiCardLinks.WorkerDetailsCard("responses", workerId),
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
            Key = "duplicates",
            Href = KpiCardLinks.WorkerDetailsCard("duplicates", workerId),
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
            Key = "errors",
            Href = KpiCardLinks.WorkerDetailsCard("errors", workerId),
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
        new() { Label = "Имя воркера", Value = worker.DisplayName },
        new() { Label = "Имя ПК", Value = string.IsNullOrWhiteSpace(worker.MachineName) ? "—" : worker.MachineName },
        new() { Label = "IP-адрес", Value = extra.IpAddress },
        new()
        {
            Label = "Дата запуска",
            TimeValue = new UtcTimeDisplayModel(extra.StartedAtUtc, "datetime")
        },
        new() { Label = "Время работы", Value = uptime },
        new() { Label = "Версия воркера", Value = extra.LeadFlowVersion },
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
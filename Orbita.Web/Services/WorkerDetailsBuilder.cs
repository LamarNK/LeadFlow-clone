using Orbita.Contracts;
using Orbita.Web.Formatting;
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
        TableSortState? sort = null,
        string? accountSearchQuery = null,
        string? accountGroupId = null)
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
        var ramBasedParallelism = WorkerParallelismRules.GetMaximumConcurrentAccounts(worker.LastRamTotalMb);
        var maxConcurrentAccountsLimit = ramBasedParallelism ?? Math.Max(worker.MaxConcurrentAccounts, 1);
        var effectiveParallelism = Math.Min(worker.MaxConcurrentAccounts, maxConcurrentAccountsLimit);

        var activityChart = stats?.HourlyActivity.Count > 0
            ? DashboardChartsBuilder.FromHourlyActivity(stats.HourlyActivity)
            : DashboardChartsBuilder.FromHourlyActivity([]);

        var groupOptions = AdsPowerAccountGroupFilter.BuildOptions(
            accounts.Select(a => (a.AdsPowerGroupId, a.AdsPowerGroupName)),
            worker.AdsPowerGroups);
        var normalizedSearch = string.IsNullOrWhiteSpace(accountSearchQuery) ? null : accountSearchQuery.Trim();
        var normalizedGroupId = AdsPowerAccountGroupFilter.Normalize(accountGroupId);
        var tableSort = sort ?? TableSortState.Create("account", descending: false);
        var filteredAccounts = FilterAccounts(accounts, normalizedSearch, normalizedGroupId);
        var accountChips = FilterChipsBuilder.ForWorkerAccounts(
            worker.Id,
            normalizedSearch,
            normalizedGroupId,
            groupOptions,
            tableSort.Column,
            tableSort.Dir);

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
            InfoItems = BuildInfoItems(worker, extra, lastActivity),
            ActivityChart = activityChart,
            Events = events,
            PeriodStats = BuildPeriodStats(stats, responses, duplicates, errors),
            Accounts = filteredAccounts,
            AccountSearchQuery = normalizedSearch,
            AccountGroupId = normalizedGroupId,
            AccountGroupOptions = groupOptions,
            HasActiveAccountFilters = accountChips.Count > 0,
            ActiveAccountFilterChips = accountChips,
            Sort = tableSort,
            MaxConcurrentAccounts = effectiveParallelism,
            MaxConcurrentAccountsLimit = maxConcurrentAccountsLimit,
            AdsPowerApiBaseUrl = worker.AdsPowerApiBaseUrl,
            AdsPowerApiKey = worker.AdsPowerApiKey,
            RuCaptchaApiKey = worker.RuCaptchaApiKey,
            MultiloginLauncherUrl = worker.MultiloginLauncherUrl,
            MultiloginCloudApiUrl = worker.MultiloginCloudApiUrl,
            HasMultiloginAutomationToken = worker.HasMultiloginAutomationToken,
            AdsPowerGroupId = worker.AdsPowerGroupId,
            AdsPowerGroupName = worker.AdsPowerGroupName,
            AdsPowerGroups = BuildAdsPowerGroupOptions(
                worker.AdsPowerGroups,
                worker.AdsPowerGroupId,
                worker.AdsPowerGroupName),
            ResponseFilterEnabled = worker.ResponseFilterEnabled,
            ResponseFilterExcludeFemale = worker.ResponseFilterExcludeFemale,
            ResponseFilterExcludeMale = worker.ResponseFilterExcludeMale,
            ResponseFilterMaxAgeMale = worker.ResponseFilterMaxAgeMale ?? worker.ResponseFilterMaxAge,
            ResponseFilterMaxAgeFemale = worker.ResponseFilterMaxAgeFemale ?? worker.ResponseFilterMaxAge,
            ResponseFilterMaxAgeDays = worker.ResponseFilterMaxResponseAgeDays,
            ResponseHighlightEnabled = worker.ResponseHighlightEnabled,
            ResponseHighlightAgeBuckets = ResponseHighlightRules.NormalizeBucketsCsv(worker.ResponseHighlightAgeBuckets),
            ResponseHighlightTargetsJson = ResponseHighlightRules.NormalizeTargetsJson(worker.ResponseHighlightTargetsJson),
            ResponseHighlightBucketOptions = ResponseHighlightRules.HighlightAgeBucketOptions,
            AutoScheduleEnabled = worker.AutoScheduleEnabled,
            AutoScheduleDays = WorkerScheduleRules.NormalizeDaysCsv(worker.AutoScheduleDays),
            AutoScheduleFromLocalTime = WorkerScheduleRules.NormalizeTime(worker.AutoScheduleFromLocalTime),
            AutoScheduleToLocalTime = WorkerScheduleRules.NormalizeTime(worker.AutoScheduleToLocalTime),
            AutoScheduleDayOptions = WorkerScheduleRules.AllowedDays,
            MessengerAutoReplyEnabled = worker.MessengerAutoReplyEnabled,
            MessengerAutoReplyMessage = worker.MessengerAutoReplyMessage,
            PhoneUnchangedHours = worker.PhoneUnchangedHours
                ?? ResponsePhoneWatchRules.DefaultUnchangedHours,
            AutoDeliverToCrm = worker.AutoDeliverToCrm,
            AutoDeliverToBitrix = worker.AutoDeliverToBitrix,
            OfficeId = worker.OfficeId,
            OfficeName = worker.OfficeName,
            System = BuildSystemPanel(worker, extra),
            CurrentActivity = WorkerActivityPresenter.Present(
                worker.CurrentActivity,
                worker.IsOnline,
                worker.ActiveAccounts ?? worker.CurrentActivity?.ActiveAccounts),
            ActiveAccountActivities = WorkerActivityPresenter.PresentActiveAccounts(
                worker.ActiveAccounts ?? worker.CurrentActivity?.ActiveAccounts,
                worker.IsOnline)
        };
    }

    private static IReadOnlyList<WorkerAccountRowViewModel> FilterAccounts(
        IReadOnlyList<WorkerAccountRowViewModel> accounts,
        string? searchQuery,
        string? groupId)
    {
        IEnumerable<WorkerAccountRowViewModel> query = accounts;
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            query = query.Where(a =>
                SearchQueryNormalizer.MatchesTokens(
                    searchQuery,
                    a.DisplayName,
                    a.AdsPowerProfileId,
                    a.AdsPowerGroupName));
        }

        if (!string.IsNullOrWhiteSpace(groupId))
        {
            query = query.Where(a => AdsPowerAccountGroupFilter.Matches(groupId, a.AdsPowerGroupId));
        }

        return query.ToList();
    }

    public static WorkerAccountRowViewModel MapAccount(
        WorkerAccountDto account,
        WorkerBalanceDto? balance,
        Guid workerId,
        IReadOnlyList<WorkerActiveAccountDto>? activeAccounts = null,
        bool workerIsOnline = false)
    {
        var (label, tone) = AccountStatusMapper.ForWorkerDetails(account.Status, account.IsEnabledInPanel);
        var responses = account.TodayResponses;
        var metricLinks = AccountMetricLinks.Hrefs(workerId, account.AccountId);
        var subProfiles = SubProfileViewModelMapper.Map(
            account.SubProfiles,
            balance?.SubProfiles,
            workerId,
            account.AccountId,
            workerIsOnline,
            null,
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
        var processing = WorkerActivityPresenter.PresentForAccount(
            null,
            workerIsOnline,
            account.AccountId,
            activeAccounts);
        return new WorkerAccountRowViewModel
        {
            Id = account.AccountId,
            DisplayName = account.DisplayName,
            IsEnabledInPanel = account.IsEnabledInPanel,
            AdsPowerProfileId = account.AdsPowerProfileId,
            AdsPowerGroupId = account.AdsPowerGroupId,
            AdsPowerGroupName = account.AdsPowerGroupName,
            HasAvitoCredentials = account.HasAvitoCredentials,
            AvitoLogin = account.AvitoLogin,
            StatusLabel = label,
            StatusTone = tone,
            Balance = balance?.TotalBalance,
            BalanceText = balance is null
                ? "—"
                : FormatAccountBalanceText(balance),
            Responses = responses,
            LastActivityUtc = account.LastActivityUtc ?? account.LastMonitoringAt,
            Errors = errors,
            ResponsesLink = metricLinks.Responses,
            ErrorsLink = metricLinks.Errors,
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
            ProcessingSubProfileId = processing.SubProfileId
        };
    }

    private static IReadOnlyList<AdsPowerGroupDto> BuildAdsPowerGroupOptions(
        IReadOnlyList<AdsPowerGroupDto>? groups,
        string? selectedGroupId,
        string? selectedGroupName)
    {
        var result = (groups ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.GroupId))
            .GroupBy(x => x.GroupId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(selectedGroupId)
            && result.All(x => !string.Equals(x.GroupId, selectedGroupId, StringComparison.Ordinal)))
        {
            result.Insert(
                0,
                new AdsPowerGroupDto(
                    selectedGroupId,
                    string.IsNullOrWhiteSpace(selectedGroupName) ? selectedGroupId : selectedGroupName));
        }

        return result;
    }

    private static string FormatAccountBalanceText(WorkerBalanceDto balance)
    {
        var lines = new List<string> { $"Аванс {balance.TotalBalance:N0} ₽" };
        var subtitle = BalanceDisplay.FormatAccountBreakdown(
            balance.TotalWalletBalance > 0 ? balance.TotalWalletBalance : null,
            BalanceDisplay.ResolveAdvanceDurationHint(
                balance.SubProfiles.Select(s => (s.Balance, s.AdvanceDurationText)).ToList()));
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            lines.Add(subtitle);
        }

        return string.Join("\n", lines);
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
        DateTime? lastActivity) =>
    [
        new() { Label = "Статус", Value = worker.IsOnline ? "Онлайн" : "Оффлайн" },
        new() { Label = "ID воркера", Value = worker.Id.ToString() },
        new() { Label = "Имя воркера", Value = worker.DisplayName },
        new()
        {
            Label = "Дата запуска",
            TimeValue = new UtcTimeDisplayModel(extra.StartedAtUtc, "datetime")
        },
        new()
        {
            Label = "Последняя активность",
            TimeValue = new UtcTimeDisplayModel(lastActivity, "activity")
        }
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

    private static WorkerSystemPanelViewModel BuildSystemPanel(WorkerDetail worker, WorkerExtraInfoViewModel extra) =>
        new()
        {
            CpuPercent = worker.LastCpuPercent,
            RamPercent = worker.LastRamPercent,
            RamUsedMb = worker.LastRamUsedMb,
            RamTotalMb = worker.LastRamTotalMb,
            MachineName = string.IsNullOrWhiteSpace(worker.MachineName) ? "—" : worker.MachineName,
            IpAddress = extra.IpAddress,
            OperatingSystem = extra.OperatingSystem,
            LeadFlowVersion = extra.LeadFlowVersion,
            AgentVersion = extra.AgentVersion,
            ConnectionCheck = extra.ConnectionCheck
        };

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

using System.Globalization;
using System.Text.RegularExpressions;
using Orbita.Contracts;
using Orbita.Web.Formatting;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class StatisticsIndexBuilder
{
    public static StatisticsViewModel Build(
        OfficeStatisticsDto data,
        DashboardPeriod period,
        IOfficeContext officeContext,
        StatisticsFiltersViewModel filters,
        IReadOnlyList<EventFilterOptionViewModel> workerOptions,
        IReadOnlyList<EventFilterOptionViewModel> accountOptions,
        IReadOnlyList<ActiveFilterChipViewModel> activeFilterChips)
    {
        var accountStats = new AccountStatsViewModel
        {
            Total = data.Accounts.Total,
            Active = data.Accounts.StatusCounts.Active,
            Inactive = data.Accounts.StatusCounts.Inactive,
            Blocked = data.Accounts.StatusCounts.Blocked,
            Errors = data.Accounts.StatusCounts.Errors
        };

        var maxBalance = data.Balances.Accounts.Count > 0
            ? Math.Max(1m, data.Balances.Accounts.Max(a => a.Advance))
            : 1m;

        var balanceRows = data.Balances.Accounts
            .Select(a => MapBalanceRow(a, maxBalance, includeSubProfiles: true))
            .ToList();

        var periodErrors = data.Responses.Errors + data.Responses.ActionRequired;
        var summary = new StatisticsSummaryViewModel
        {
            ActiveAdsCount = data.Accounts.ActiveAdsCount,
            BlockedAdsCount = data.Accounts.BlockedAdsCount,
            PeriodTotal = data.Responses.Total,
            PeriodSent = data.Responses.Sent,
            PeriodDuplicates = data.Responses.Duplicates,
            PeriodErrors = periodErrors,
            PeriodUnique = data.Responses.Unique,
            WorkersOnline = data.Workers.Online,
            WorkersTotal = data.Workers.Total,
            TotalAdvanceText = BalanceDisplay.FormatAmount(data.Balances.TotalAdvance),
            AvgResponseMinutesText = data.Responses.AvgResponseMinutes is double minutes
                ? $"{Math.Round(minutes, 0):0} мин"
                : null,
            BalanceAccountCount = data.Balances.Accounts.Count,
            LowBalanceAccountCount = data.Balances.LowBalanceAccountCount,
            LowBalanceHiddenCount = 0
        };

        var header = PageHeaderBuilder.WithOfficeScope(
            PageHeaderBuilder.Statistics(period),
            officeContext);

        return new StatisticsViewModel
        {
            Header = header,
            Filters = filters,
            WorkerOptions = workerOptions,
            AccountOptions = accountOptions,
            HasActiveFilters = HasActiveFilters(filters),
            ActiveFilterChips = activeFilterChips,
            KpiCards = BuildKpiCards(data, period, filters),
            BalanceRows = balanceRows,
            Charts = BuildCharts(data, accountStats),
            AccountStats = accountStats,
            Workers = data.Workers.Items
                .Select(w => new StatisticsWorkerRowViewModel
                {
                    Id = w.Id,
                    DisplayName = w.DisplayName,
                    OfficeName = w.OfficeName,
                    IsOnline = w.IsOnline,
                    PeriodResponses = w.PeriodResponses,
                    PeriodSent = w.PeriodSent,
                    PeriodDuplicates = w.PeriodDuplicates,
                    PeriodErrors = w.PeriodErrors,
                    ActiveAccounts = w.ActiveAccounts,
                    TotalAccounts = w.TotalAccounts
                })
                .ToList(),
            BitrixDeliveries = MapBitrixDeliveries(data.BitrixDeliveries, period, filters),
            CrmDeliveries = MapCrmDeliveries(data.CrmDeliveries, period, filters),
            HrInsights = MapHrInsights(data.HrInsights),
            MonitoringCycles = MapMonitoringCycles(data.MonitoringCycles),
            Summary = summary,
            ShowOfficeColumn = officeContext.ShowOfficeColumn
        };
    }

    public static bool HasActiveFilters(StatisticsFiltersViewModel filters) =>
        filters.WorkerIds.Count > 0
        || filters.AccountIds.Count > 0
        || !string.IsNullOrWhiteSpace(filters.VacancyQuery);

    private static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(
        OfficeStatisticsDto data,
        DashboardPeriod period,
        StatisticsFiltersViewModel filters)
    {
        var totalResponses = Math.Max(1, data.Responses.Total);
        var periodErrors = data.Responses.Errors + data.Responses.ActionRequired;
        string Pct(int value) => $"{value * 100.0 / totalResponses:0.#}%";
        var sentShare = data.Responses.Total == 0
            ? "0%"
            : Pct(data.Responses.Sent);

        return
        [
            new()
            {
                Key = "responses",
                Href = KpiCardLinks.StatisticsCard("responses", period.From, period.To, filters),
                Label = "Откликов",
                Value = data.Responses.Total.ToString(),
                CountValue = data.Responses.Total,
                Delta = period.Label,
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-comments",
                IconTone = "blue"
            },
            new()
            {
                Key = "sent",
                Href = KpiCardLinks.StatisticsCard("sent", period.From, period.To, filters),
                Label = "В Битрикс24",
                Value = data.Responses.Sent.ToString(),
                CountValue = data.Responses.Sent,
                Delta = sentShare,
                DeltaTone = data.Responses.Sent > 0 ? "good" : "neutral",
                IconClass = "fa-solid fa-paper-plane",
                IconTone = "green"
            },
            new()
            {
                Key = "duplicates",
                Href = KpiCardLinks.StatisticsCard("duplicates", period.From, period.To, filters),
                Label = "Дублей",
                Value = data.Responses.Duplicates.ToString(),
                CountValue = data.Responses.Duplicates,
                Delta = Pct(data.Responses.Duplicates),
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-clone",
                IconTone = "orange"
            },
            new()
            {
                Key = "errors",
                Href = KpiCardLinks.StatisticsCard("errors", period.From, period.To, filters),
                Label = "Ошибок",
                Value = periodErrors.ToString(),
                CountValue = periodErrors,
                Delta = Pct(periodErrors),
                DeltaTone = periodErrors > 0 ? "bad" : "good",
                IconClass = "fa-solid fa-triangle-exclamation",
                IconTone = "orange"
            },
            new()
            {
                Key = "unique",
                Href = KpiCardLinks.StatisticsCard("unique", period.From, period.To, filters),
                Label = "Уникальных",
                Value = data.Responses.Unique.ToString(),
                CountValue = data.Responses.Unique,
                Delta = Pct(data.Responses.Unique),
                DeltaTone = "good",
                IconClass = "fa-regular fa-circle-check",
                IconTone = "green"
            }
        ];
    }

    private static StatisticsChartsViewModel BuildCharts(
        OfficeStatisticsDto data,
        AccountStatsViewModel accountStats)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        return new StatisticsChartsViewModel
        {
            DailyTrend = new StackedDailyChartViewModel
            {
                Labels = data.DailyTrend
                    .Select(d => d.DateLocal.ToString("dd.MM", culture))
                    .ToList(),
                Sent = data.DailyTrend.Select(d => d.Sent).ToList(),
                InProgress = data.DailyTrend.Select(d => d.InProgress).ToList(),
                ActionRequired = data.DailyTrend.Select(d => d.ActionRequired).ToList(),
                Duplicates = data.DailyTrend.Select(d => d.Duplicates).ToList(),
                Errors = data.DailyTrend.Select(d => d.Errors).ToList(),
                Totals = data.DailyTrend.Select(d => d.Total).ToList()
            },
            AccountStatus = new DonutChartViewModel
            {
                Total = accountStats.Total,
                Active = accountStats.Active,
                Inactive = accountStats.Inactive,
                Blocked = accountStats.Blocked,
                Errors = accountStats.Errors
            }
        };
    }

    private static IReadOnlyList<BitrixDeliveryStatRowViewModel> MapBitrixDeliveries(
        IReadOnlyList<BitrixDeliveryStatDto> rows,
        DashboardPeriod period,
        StatisticsFiltersViewModel filters) =>
        rows.Select(row => new BitrixDeliveryStatRowViewModel
        {
            BitrixInstanceId = row.BitrixInstanceId,
            Label = row.Label,
            SentCount = row.SentCount,
            ResponsesUrl = KpiCardLinks.Responses(
                period.From,
                period.To,
                status: "sent",
                workerId: filters.WorkerIds.FirstOrDefault(),
                accountId: filters.AccountIds.FirstOrDefault(),
                bitrixDestination: row.BitrixInstanceId.ToString()) ?? "/Responses"
        }).ToList();

    private static IReadOnlyList<CrmDeliveryStatRowViewModel> MapCrmDeliveries(
        IReadOnlyList<CrmDeliveryStatDto> rows,
        DashboardPeriod period,
        StatisticsFiltersViewModel filters) =>
        rows.Select(row => new CrmDeliveryStatRowViewModel
        {
            OfficeId = row.OfficeId,
            Label = row.Label,
            SentCount = row.SentCount,
            ResponsesUrl = KpiCardLinks.Responses(
                period.From,
                period.To,
                status: "sent",
                workerId: filters.WorkerIds.FirstOrDefault(),
                accountId: filters.AccountIds.FirstOrDefault()) ?? "/Responses"
        }).ToList();

    private static HrInsightsViewModel MapHrInsights(HrInsightsDto insights) =>
        new()
        {
            TopCities = MapHrMetrics(insights.TopCities),
            TopVacancies = MapHrMetrics(insights.TopVacancies),
            TopAccounts = MapHrMetrics(insights.TopAccounts),
            AgeBuckets = insights.AgeBuckets
                .Select(b => new AgeBucketRowViewModel
                {
                    Bucket = b.Bucket,
                    Total = b.Total,
                    Sent = b.Sent,
                    ConversionText = b.ConversionText
                })
                .ToList(),
            AverageAgeText = insights.AverageAgeText,
            MessengerCoverageText = insights.MessengerCoverageText
        };

    private static IReadOnlyList<HrMetricRowViewModel> MapHrMetrics(IReadOnlyList<HrMetricDto> rows) =>
        rows.Select(r => new HrMetricRowViewModel
        {
            Name = r.Name,
            Total = r.Total,
            Sent = r.Sent,
            ConversionText = r.ConversionText,
            ShareText = r.ShareText
        }).ToList();

    private static StatisticsBalanceRowViewModel MapBalanceRow(
        AccountBalanceStatDto account,
        decimal maxBalance,
        bool includeSubProfiles)
    {
        var subProfiles = includeSubProfiles ? MapSubProfileBalances(account.SubProfiles) : [];
        var durationHint = BalanceDisplay.ResolveAdvanceDurationHint(
            account.SubProfiles.Select(s => (s.Balance, s.AdvanceDurationText)).ToList());

        return new StatisticsBalanceRowViewModel
        {
            AccountId = account.AccountId,
            AccountName = account.AccountName,
            WorkerId = account.WorkerId,
            WorkerName = account.WorkerName,
            OfficeName = account.OfficeName,
            Advance = account.Advance,
            Wallet = account.Wallet,
            AdvanceText = BalanceDisplay.FormatAmount(account.Advance),
            WalletText = account.Wallet > 0 ? BalanceDisplay.FormatAmount(account.Wallet) : "—",
            BalanceBreakdown = includeSubProfiles
                ? SubProfileViewModelMapper.BuildBalanceBreakdown(
                    SubProfileViewModelMapper.MapFromBalances(account.SubProfiles))
                : null,
            BalanceSubtitle = subProfiles.Count == 0
                ? BalanceDisplay.FormatAccountBreakdown(
                    account.Wallet > 0 ? account.Wallet : null,
                    durationHint)
                : null,
            SubProfiles = subProfiles,
            IsLowBalance = account.IsLowBalance,
            BarWidth = (double)(account.Advance / maxBalance)
        };
    }

    private static MonitoringCycleReportViewModel MapMonitoringCycles(MonitoringCycleReportDto? report)
    {
        if (report is null)
        {
            return MonitoringCycleReportViewModel.Empty;
        }

        var leadSummaries = report.LeadSummaries
            .Select(x => new MonitoringCycleLeadSummaryViewModel
            {
                AccountName = x.AccountName,
                TotalLeads = x.TotalLeads,
                BreakdownText = x.Breakdown.Count == 0 ? "откликов не найдено" : string.Join("; ", x.Breakdown)
            })
            .ToList();

        return new MonitoringCycleReportViewModel
        {
            IsDetailed = report.IsDetailed,
            HasData = report.LeadSummaries.Count > 0
                || report.AccountReports.Count > 0
                || report.NotStartedSummaries.Count > 0
                || report.TotalLeads > 0,
            TotalLeads = report.TotalLeads,
            TotalCaptcha = report.TotalCaptcha,
            TotalCaptchaSolved = report.TotalCaptchaSolved,
            AccountsWithNotStarted = report.AccountsWithNotStarted,
            NotStartedPositions = report.NotStartedPositions,
            ZeroLeadAccountCount = leadSummaries.Count(x => x.TotalLeads == 0),
            NotStartedSummaries = report.NotStartedSummaries,
            NotStartedRows = ParseNotStartedSummaries(report.NotStartedSummaries),
            LeadSummaries = leadSummaries
                .Where(x => x.TotalLeads > 0)
                .ToList(),
            AccountReports = report.AccountReports
                .Select(account => new MonitoringCycleAccountReportViewModel
                {
                    AccountName = account.AccountName,
                    DateUtc = account.DateUtc,
                    HeaderText =
                        $"{account.AccountName} — {account.SubProfileCount} суб-профилей, {FormatCycleCount(account.CycleCount)}" +
                        $", откликов: {account.TotalLeads}",
                    SubProfileCount = account.SubProfileCount,
                    CycleCount = account.CycleCount,
                    TotalLeads = account.TotalLeads,
                    TotalCaptcha = account.TotalCaptcha,
                    TotalCaptchaSolved = account.TotalCaptchaSolved,
                    CaptchaText = FormatCaptchaSummary(account.TotalCaptcha, account.TotalCaptchaSolved),
                    Rows = account.Rows
                        .Select(row => new MonitoringCycleSubProfileRowViewModel
                        {
                            PositionText = $"{row.Position}/{row.TotalPositions}",
                            Name = row.Name,
                            CompletionTimesUtc = row.CompletionTimesUtc,
                            LeadsText = row.LeadsPerCycle.Count == 0
                                ? "—"
                                : string.Join(", ", row.LeadsPerCycle),
                            CaptchaEvents = (row.CaptchaPerCycle ?? [])
                                .Select(captcha => new MonitoringCycleCaptchaViewModel
                                {
                                    TimestampUtc = captcha.TimestampUtc,
                                    Status = captcha.Status,
                                    Unsolved = captcha.Unsolved
                                })
                                .ToList(),
                            Errors = row.Errors
                                .Select(error => new MonitoringCycleErrorViewModel
                                {
                                    TimestampUtc = error.TimestampUtc,
                                    Detail = error.Detail
                                })
                                .ToList(),
                            Passes = MapPasses(row),
                            HasErrors = row.Errors.Count > 0,
                            HasNotStarted = !row.WasStarted,
                            NotStartedReason = row.NotStartedReason,
                            NotStartedAtUtc = row.NotStartedAtUtc
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private static IReadOnlyList<MonitoringCyclePassViewModel> MapPasses(MonitoringCycleSubProfileRowDto row)
    {
        if (row.Passes is { Count: > 0 } passes)
        {
            return passes
                .Select(pass => new MonitoringCyclePassViewModel
                {
                    TimestampUtc = pass.TimestampUtc,
                    Completed = pass.Completed,
                    InProgress = pass.InProgress,
                    HasCollected = pass.HasCollected,
                    CollectedCount = pass.CollectedCount,
                    CaptchaStatus = pass.CaptchaStatus,
                    CaptchaUnsolved = pass.CaptchaUnsolved,
                    ErrorDetail = pass.ErrorDetail,
                    Skipped = pass.Skipped
                })
                .ToList();
        }

        var fallback = new List<MonitoringCyclePassViewModel>(
            row.CompletionTimesUtc.Count + row.Errors.Count);
        for (var i = 0; i < row.CompletionTimesUtc.Count; i++)
        {
            var hasCollected = i < row.LeadsPerCycle.Count;
            var collected = 0;
            if (hasCollected)
            {
                _ = int.TryParse(row.LeadsPerCycle[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out collected);
            }

            fallback.Add(new MonitoringCyclePassViewModel
            {
                TimestampUtc = row.CompletionTimesUtc[i],
                Completed = true,
                HasCollected = hasCollected,
                CollectedCount = collected
            });
        }

        foreach (var error in row.Errors)
        {
            fallback.Add(new MonitoringCyclePassViewModel
            {
                TimestampUtc = error.TimestampUtc,
                ErrorDetail = error.Detail
            });
        }

        fallback.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return fallback;
    }

    private static string FormatCaptchaSummary(int seen, int solved)
    {
        if (seen <= 0)
        {
            return "—";
        }

        if (solved >= seen)
        {
            return seen == 1 ? "решена" : $"{seen} решены";
        }

        if (solved <= 0)
        {
            return seen == 1 ? "не решена" : $"{seen} не решены";
        }

        return $"решено {solved}/{seen}";
    }

    private static readonly Regex NotStartedSummaryRegex = new(
        @"^(\d+)\s+не запущены\s+—\s*(.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static IReadOnlyList<MonitoringCycleNotStartedRowViewModel> ParseNotStartedSummaries(
        IReadOnlyList<string> summaries)
    {
        if (summaries.Count == 0)
        {
            return [];
        }

        return summaries
            .Select(line =>
            {
                var trimmed = line.Trim();
                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex < 0)
                {
                    return new MonitoringCycleNotStartedRowViewModel
                    {
                        AccountName = trimmed,
                        PositionsText = string.Empty
                    };
                }

                var accountName = trimmed[..colonIndex].Trim();
                var rest = trimmed[(colonIndex + 1)..].Trim();
                var match = NotStartedSummaryRegex.Match(rest);
                if (!match.Success)
                {
                    return new MonitoringCycleNotStartedRowViewModel
                    {
                        AccountName = accountName,
                        PositionsText = rest
                    };
                }

                return new MonitoringCycleNotStartedRowViewModel
                {
                    AccountName = accountName,
                    NotStartedCount = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    PositionsText = match.Groups[2].Value.Trim()
                };
            })
            .ToList();
    }

    private static string FormatCycleCount(int count) =>
        count switch
        {
            1 => "1 цикл",
            >= 2 and <= 4 => $"{count} цикла",
            _ => $"{count} циклов"
        };

    private static IReadOnlyList<StatisticsSubProfileBalanceViewModel> MapSubProfileBalances(
        IReadOnlyList<SubProfileBalanceDto> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var maxAdvance = Math.Max(1m, items.Max(s => s.Balance ?? 0m));

        return items
            .Select((item, index) =>
            {
                var name = string.IsNullOrWhiteSpace(item.SubProfileName)
                    ? items.Count == 1 ? "Субпрофиль" : $"Субпрофиль {index + 1}"
                    : item.SubProfileName.Trim();
                var advance = item.Balance ?? 0m;
                var wallet = item.WalletBalance ?? 0m;

                return new StatisticsSubProfileBalanceViewModel
                {
                    Name = name,
                    AdvanceText = item.Balance.HasValue ? BalanceDisplay.FormatAmount(item.Balance) : "—",
                    WalletText = wallet > 0 ? BalanceDisplay.FormatAmount(wallet) : null,
                    DurationText = string.IsNullOrWhiteSpace(item.AdvanceDurationText)
                        ? null
                        : item.AdvanceDurationText.Trim(),
                    IsLowBalance = item.Balance is decimal balance
                        && balance > 0
                        && balance < BalanceDisplayRules.LowBalanceThresholdRub,
                    BarWidth = (double)(advance / maxAdvance)
                };
            })
            .ToList();
    }
}

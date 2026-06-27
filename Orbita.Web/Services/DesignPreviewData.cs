using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class DesignPreviewData
{
    public static readonly Guid WorkerMoscowId = Guid.Parse("11111111-1111-1111-1111-111111111101");
    public static readonly Guid WorkerSpbId = Guid.Parse("11111111-1111-1111-1111-111111111102");
    public static readonly Guid WorkerKazanId = Guid.Parse("11111111-1111-1111-1111-111111111103");

    private static readonly Guid[] PreviewWorkerIds =
    [
        WorkerMoscowId,
        WorkerSpbId,
        WorkerKazanId,
        Guid.Parse("11111111-1111-1111-1111-111111111104"),
        Guid.Parse("11111111-1111-1111-1111-111111111105"),
        Guid.Parse("11111111-1111-1111-1111-111111111106"),
        Guid.Parse("11111111-1111-1111-1111-111111111107"),
        Guid.Parse("11111111-1111-1111-1111-111111111108"),
        Guid.Parse("11111111-1111-1111-1111-111111111109"),
        Guid.Parse("11111111-1111-1111-1111-111111111110"),
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("11111111-1111-1111-1111-111111111112")
    ];

    public static readonly Guid AccountAlphaId = Guid.Parse("22222222-2222-2222-2222-222222222201");
    public static readonly Guid AccountBetaId = Guid.Parse("22222222-2222-2222-2222-222222222202");
    public static readonly Guid AccountGammaId = Guid.Parse("22222222-2222-2222-2222-222222222203");

    private static readonly DateTime Now = DateTime.UtcNow;

    public static GlobalDashboardSummary Summary => new(
        TotalWorkers: 3,
        OnlineWorkers: 3,
        TotalToday: 1234,
        SentToCrm: 1100,
        InProgress: 42,
        Duplicates: 256,
        Errors: 18,
        ActionRequired: 0,
        ConnectedAccounts: 30,
        RequiresAuthorization: 0,
        AccountsNeedAttentionCount: 0,
        ActiveAdsCount: 120,
        BlockedAdsCount: 0,
        TotalBalance: 48_500m,
        HourlyActivity: BuildHourly(),
        WeeklyByDayActivity: BuildWeekly(),
        AggregatedAtUtc: Now);

    public static IReadOnlyList<WorkerListItem> Workers => BuildWorkerListItems();

    public static WorkersIndexViewModel BuildWorkersIndexViewModel(string? searchQuery, int page, int pageSize)
    {
        var rows = BuildWorkerRows();
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var q = searchQuery.Trim();
            rows = rows
                .Where(w => w.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var total = rows.Count;
        var paged = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var online = rows.Count(w => w.IsOnline);
        var offline = rows.Count - online;

        return new WorkersIndexViewModel
        {
            SearchQuery = searchQuery,
            KpiCards =
            [
                new() { Label = "Всего воркеров", Value = total.ToString(), CountValue = total, IconClass = "fa-solid fa-server", IconTone = "blue" },
                new() { Label = "Онлайн", Value = online.ToString(), CountValue = online, IconClass = "fa-solid fa-circle-check", IconTone = "green" },
                new() { Label = "Оффлайн", Value = offline.ToString(), CountValue = offline, IconClass = "fa-solid fa-circle-xmark", IconTone = "orange" },
                new() { Label = "Всего откликов", Value = rows.Sum(w => w.Responses).ToString(), CountValue = rows.Sum(w => w.Responses), IconClass = "fa-regular fa-comments", IconTone = "blue" },
                new() { Label = "Ошибок", Value = rows.Sum(w => w.Errors).ToString(), CountValue = rows.Sum(w => w.Errors), IconClass = "fa-solid fa-triangle-exclamation", IconTone = "orange" }
            ],
            Workers = paged,
            Pagination = new PaginationViewModel { Page = page, PageSize = pageSize, TotalItems = total }
        };
    }

    private static IReadOnlyList<WorkerListItem> BuildWorkerListItems() =>
        BuildWorkerRows().Select((w, i) => new WorkerListItem(
            w.Id,
            w.DisplayName,
            $"WIN-W{(i + 1):D2}",
            "2.4.1",
            w.IsOnline ? "Running" : "Stopped",
            w.IsOnline ? null : "Нет heartbeat",
            w.IsOnline,
            w.IsOnline,
            w.LastActivityUtc?.ToUniversalTime(),
            w.TotalAccounts,
            w.Responses,
            w.Errors)).ToList();

    private static IReadOnlyList<WorkerRowViewModel> BuildWorkerRows()
    {
        DateTime?[] times =
        [
            Now.AddSeconds(-12), Now.AddSeconds(-8), Now.AddSeconds(-15),
            Now.AddMinutes(-2), Now.AddMinutes(-4), Now.AddMinutes(-6),
            Now.AddMinutes(-9), Now.AddMinutes(-11), Now.AddMinutes(-14),
            Now.AddMinutes(-22), Now.AddHours(-1), null
        ];

        var accounts = new (int Active, int Total)[]
        {
            (10, 10), (10, 10), (10, 10), (10, 10), (8, 10), (10, 10),
            (9, 10), (10, 10), (7, 10), (10, 10), (6, 10), (0, 10)
        };

        var responses = new[] { 432, 401, 401, 388, 356, 342, 318, 295, 271, 248, 192, 0 };
        var duplicates = new[] { 98, 87, 71, 64, 58, 52, 47, 41, 36, 29, 18, 0 };
        var errors = new[] { 5, 8, 5, 4, 6, 3, 2, 4, 1, 2, 3, 0 };
        var online = new[] { true, true, true, true, true, true, true, true, true, true, true, false };

        return Enumerable.Range(0, 12).Select(i => new WorkerRowViewModel
        {
            Id = PreviewWorkerIds[i],
            DisplayName = $"Worker #{i + 1}",
            IsOnline = online[i],
            ActiveAccounts = accounts[i].Active,
            TotalAccounts = accounts[i].Total,
            Responses = responses[i],
            Duplicates = duplicates[i],
            Errors = errors[i],
            LastActivityUtc = times[i]
        }).ToList();
    }

    public static DashboardViewModel BuildDashboardViewModel()
    {
        var updatedAt = Now;
        var kpiCards = (IReadOnlyList<DashboardKpiCardViewModel>)
        [
            new()
                {
                    Label = "Откликов всего",
                    Value = "1234",
                    CountValue = 1234,
                    Delta = "+12.4%",
                    DeltaTone = "good",
                    IconClass = "fa-regular fa-comments",
                    IconTone = "blue",
                    Sparkline = SparklineGenerator.Create(1101, SparklineTrend.Up),
                    SparkColor = "#2563eb"
                },
                new()
                {
                    Label = "Дублей",
                    Value = "256",
                    CountValue = 256,
                    Delta = "-5.3%",
                    DeltaTone = "good",
                    IconClass = "fa-regular fa-clone",
                    IconTone = "green",
                    Sparkline = SparklineGenerator.Create(1102, SparklineTrend.Down),
                    SparkColor = "#16a34a"
                },
                new()
                {
                    Label = "Ошибок",
                    Value = "18",
                    CountValue = 18,
                    Delta = "+2.1%",
                    DeltaTone = "bad",
                    IconClass = "fa-solid fa-triangle-exclamation",
                    IconTone = "orange",
                    Sparkline = SparklineGenerator.Create(1103, SparklineTrend.UpGentle),
                    SparkColor = "#f59e0b"
                },
                new()
                {
                    Label = "Аккаунтов активно",
                    Value = "30 / 30",
                    CountValue = 30,
                    ValueSuffix = " / 30",
                    Delta = "100%",
                    DeltaTone = "good",
                    IconClass = "fa-regular fa-user",
                    IconTone = "purple",
                    Sparkline = SparklineGenerator.Create(1104, SparklineTrend.Up),
                    SparkColor = "#7c3aed"
                },
                new()
                {
                    Label = "Воркеров онлайн",
                    Value = "3 / 3",
                    CountValue = 3,
                    ValueSuffix = " / 3",
                    Delta = "100%",
                    DeltaTone = "good",
                    IconClass = "fa-solid fa-server",
                    IconTone = "blue",
                    Sparkline = SparklineGenerator.Create(1105, SparklineTrend.Up),
                    SparkColor = "#2563eb"
                }
        ];
        var hourlyChart = BuildHourlyChart();
        var accountStats = new AccountStatsViewModel
        {
            Total = 30,
            Active = 30,
            Inactive = 0,
            Blocked = 0,
            Errors = 0
        };

        return new DashboardViewModel
        {
            Header = new PageHeaderViewModel
            {
                Title = "Панель управления",
                Subtitle = "Общая сводка по всем воркерам",
                ShowRefresh = true,
                ShowDateRange = true,
                UpdatedAtUtc = updatedAt,
                DateRangeLabel = $"{DateTime.Today:dd.MM.yyyy} — {DateTime.Today:dd.MM.yyyy}"
            },
            KpiCards = kpiCards,
            Workers =
            [
                new()
                {
                    Id = WorkerMoscowId,
                    DisplayName = "Worker #1",
                    IsOnline = true,
                    ActiveAccounts = 10,
                    TotalAccounts = 10,
                    Responses = 432,
                    Duplicates = 98,
                    Errors = 5,
                    LastActivityUtc = updatedAt.AddSeconds(-12)
                },
                new()
                {
                    Id = WorkerSpbId,
                    DisplayName = "Worker #2",
                    IsOnline = true,
                    ActiveAccounts = 10,
                    TotalAccounts = 10,
                    Responses = 401,
                    Duplicates = 87,
                    Errors = 8,
                    LastActivityUtc = updatedAt.AddSeconds(-8)
                },
                new()
                {
                    Id = WorkerKazanId,
                    DisplayName = "Worker #3",
                    IsOnline = true,
                    ActiveAccounts = 10,
                    TotalAccounts = 10,
                    Responses = 401,
                    Duplicates = 71,
                    Errors = 5,
                    LastActivityUtc = updatedAt.AddSeconds(-15)
                }
            ],
            HourlyChart = hourlyChart,
            Events =
            [
                new() { Message = "Новый отклик по объявлению 12345678", Subtitle = "Аккаунт: user_01", TimeUtc = updatedAt.AddSeconds(-42), WorkerName = "Worker #1", Level = "success" },
                new() { Message = "Найден дубликат отклика", Subtitle = "Аккаунт: user_07", TimeUtc = updatedAt.AddSeconds(-75), WorkerName = "Worker #2", Level = "warning" },
                new() { Message = "Ошибка при отправке в Bitrix24", Subtitle = "Аккаунт: user_03", TimeUtc = updatedAt.AddSeconds(-108), WorkerName = "Worker #1", Level = "error" },
                new() { Message = "Баланс обновлен", Subtitle = "Аккаунт: user_05", TimeUtc = updatedAt.AddSeconds(-121), WorkerName = "Worker #3", Level = "success" },
                new() { Message = "Аккаунт успешно авторизован", Subtitle = "Аккаунт: user_08", TimeUtc = updatedAt.AddSeconds(-149), WorkerName = "Worker #2", Level = "success" }
            ],
            AccountStats = accountStats,
            Charts = DashboardChartsBuilder.FromPresentation(kpiCards, hourlyChart, accountStats)
        };
    }

    private static IReadOnlyList<DashboardChartPointViewModel> BuildHourlyChart() =>
        HourlyResponsesGenerator.BuildDailyPoints();

    public static WorkerDetail? GetWorker(Guid id)
    {
        if (id == WorkerMoscowId)
        {
            return new WorkerDetail(
                WorkerMoscowId, "Worker #1", "WIN-W01", "2.4.1",
                "Running", null, true, true, Now.AddSeconds(-12), Now.AddMinutes(8),
                new DashboardStatsDto(14, 432, 334, 42, 98, 5, 0, 10, 0, 0, 120, 0, 2, BuildHourly(), BuildWeekly()),
                BuildWorkerBalances(WorkerMoscowId));
        }

        if (id == WorkerSpbId)
        {
            return new WorkerDetail(
                WorkerSpbId, "Worker #2", "WIN-W02", "2.4.1",
                "Running", null, true, true, Now.AddMinutes(-5), Now.AddMinutes(5),
                new DashboardStatsDto(9, 62, 48, 5, 3, 2, 1, 3, 1, 1, 15, 2, 1, BuildHourly(), BuildWeekly()),
                [new(AccountGammaId, "avito_gamma", 67_400m, [new("Основной", 67_400m)])]);
        }

        if (id == WorkerKazanId)
        {
            return new WorkerDetail(
                WorkerKazanId, "Worker #3", "WIN-W03", "2.4.1",
                "Running", null, true, true, Now.AddSeconds(-15), Now.AddMinutes(5),
                new DashboardStatsDto(3, 26, 16, 2, 2, 0, 0, 2, 0, 0, 9, 0, 0, BuildHourly(), BuildWeekly()),
                []);
        }

        var row = BuildWorkerRows().FirstOrDefault(w => w.Id == id);
        if (row is null) return null;

        var index = Array.IndexOf(PreviewWorkerIds, row.Id);
        var machine = $"WIN-W{(index + 1):D2}";
        return new WorkerDetail(
            row.Id,
            row.DisplayName,
            machine,
            "2.4.1",
            row.IsOnline ? "Running" : "Stopped",
            row.IsOnline ? null : "Нет heartbeat",
            row.IsOnline,
            row.IsOnline,
            row.LastActivityUtc?.ToUniversalTime(),
            row.IsOnline ? Now.AddMinutes(5) : null,
            new DashboardStatsDto(
                6, row.Responses, row.Responses - row.Duplicates, row.Duplicates, row.Errors,
                0, 0, row.TotalAccounts, 0, 0, row.TotalAccounts, 0, 0, BuildHourly(), BuildWeekly()),
            []);
    }

    public static WorkerDetailsViewModel? BuildWorkerDetailsViewModel(Guid id)
    {
        var worker = GetWorker(id);
        if (worker is null) return null;

        var summary = BuildWorkerRows().FirstOrDefault(w => w.Id == id);
        return WorkerDetailsBuilder.Build(
            worker,
            GetWorkerAccountRows(id),
            GetWorkerEvents(id),
            GetWorkerMeta(id),
            summary);
    }

    private static IReadOnlyList<WorkerBalanceDto> BuildWorkerBalances(Guid workerId)
    {
        if (workerId == WorkerMoscowId)
        {
            return
            [
                new(AccountAlphaId, "user_01", 42_300m, [new("Основной", 42_300m)]),
                new(AccountBetaId, "user_02", 18_750m, [new("Основной", 18_750m)]),
                new(Guid.Parse("22222222-2222-2222-2222-222222222209"), "user_03", 31_200m, [new("Основной", 31_200m)]),
                new(Guid.Parse("22222222-2222-2222-2222-222222222210"), "user_04", 27_450m, [new("Основной", 27_450m)]),
                new(Guid.Parse("22222222-2222-2222-2222-222222222211"), "user_05", 19_800m, [new("Основной", 19_800m)]),
                new(Guid.Parse("22222222-2222-2222-2222-222222222212"), "user_06", 22_100m, [new("Основной", 22_100m)]),
                new(Guid.Parse("22222222-2222-2222-2222-222222222213"), "user_07", 15_600m, [new("Основной", 15_600m)]),
                new(Guid.Parse("22222222-2222-2222-2222-222222222214"), "user_08", 28_900m, [new("Основной", 28_900m)]),
                new(Guid.Parse("22222222-2222-2222-2222-222222222215"), "user_09", 12_400m, [new("Основной", 12_400m)]),
                new(Guid.Parse("22222222-2222-2222-2222-222222222216"), "user_10", 9_850m, [new("Основной", 9_850m)])
            ];
        }

        if (workerId == WorkerSpbId)
            return [new(AccountGammaId, "avito_gamma", 67_400m, [new("Основной", 67_400m)])];

        return [];
    }

    private static WorkerExtraInfoViewModel GetWorkerMeta(Guid workerId)
    {
        var index = Array.IndexOf(PreviewWorkerIds, workerId);
        if (index < 0) index = 0;

        var row = BuildWorkerRows().FirstOrDefault(w => w.Id == workerId);
        var startedAt = Now.AddDays(-2).AddHours(-14).AddMinutes(-index * 17);
        return new WorkerExtraInfoViewModel
        {
            IpAddress = $"185.22.{174 + index}.{101 + index}",
            StartedAtUtc = startedAt,
            LeadFlowVersion = "2.4.1",
            AgentVersion = "1.8.3",
            OperatingSystem = index % 3 == 0 ? "Windows Server 2022" : index % 3 == 1 ? "Windows Server 2019" : "Ubuntu 22.04 LTS",
            ConnectionCheck = row?.IsOnline == true ? $"Успешно ({12 + index} мс)" : "Нет связи"
        };
    }

    private static IReadOnlyList<WorkerAccountRowViewModel> GetWorkerAccountRows(Guid workerId)
    {
        if (workerId == WorkerMoscowId)
        {
            var responses = new[] { 58, 51, 47, 44, 39, 36, 33, 41, 38, 45 };
            var errors = new[] { 0, 0, 1, 0, 0, 0, 0, 1, 0, 0 };
            var balances = new[] { 42_300m, 18_750m, 31_200m, 27_450m, 19_800m, 22_100m, 15_600m, 28_900m, 12_400m, 9_850m };
            var tones = new[] { "success", "success", "error", "success", "success", "success", "success", "error", "success", "success" };
            var labels = new[] { "Активен", "Активен", "Ошибка", "Активен", "Активен", "Активен", "Активен", "Ошибка", "Активен", "Активен" };

            var accountIds = new[]
            {
                AccountAlphaId,
                AccountBetaId,
                Guid.Parse("22222222-2222-2222-2222-222222222209"),
                Guid.Parse("22222222-2222-2222-2222-222222222210"),
                Guid.Parse("22222222-2222-2222-2222-222222222211"),
                Guid.Parse("22222222-2222-2222-2222-222222222212"),
                Guid.Parse("22222222-2222-2222-2222-222222222213"),
                Guid.Parse("22222222-2222-2222-2222-222222222214"),
                Guid.Parse("22222222-2222-2222-2222-222222222215"),
                Guid.Parse("22222222-2222-2222-2222-222222222216")
            };

            return Enumerable.Range(1, 10).Select(i => new WorkerAccountRowViewModel
            {
                Id = accountIds[i - 1],
                DisplayName = $"user_{i:D2}",
                StatusLabel = labels[i - 1],
                StatusTone = tones[i - 1],
                BalanceText = $"{balances[i - 1]:N0} ₽",
                Responses = responses[i - 1],
                LastActivityUtc = Now.AddMinutes(-(i * 3 + 1)),
                Errors = errors[i - 1]
            }).ToList();
        }

        var row = BuildWorkerRows().FirstOrDefault(w => w.Id == workerId);
        if (row is null) return [];

        return GetAccounts(workerId).Select((a, i) =>
        {
            var balance = BuildWorkerBalances(workerId).FirstOrDefault(b => b.AccountId == a.AccountId);
            var mapped = WorkerDetailsBuilder.MapAccount(a, balance, row.Responses / Math.Max(1, row.TotalAccounts), a.LastErrorMessage is not null ? 1 : 0);
            return mapped;
        }).ToList();
    }

    private static IReadOnlyList<DashboardEventRowViewModel> GetWorkerEvents(Guid workerId)
    {
        if (workerId == WorkerMoscowId)
        {
            return
            [
                new() { Message = "Новый отклик", Subtitle = "Аккаунт user_01", TimeUtc = Now.AddMinutes(-3), Level = "success" },
                new() { Message = "Отклик отправлен в CRM", Subtitle = "Аккаунт user_02", TimeUtc = Now.AddMinutes(-5), Level = "success" },
                new() { Message = "Дубликат отклика пропущен", Subtitle = "Аккаунт user_07", TimeUtc = Now.AddMinutes(-7), Level = "warning" },
                new() { Message = "Баланс обновлён", Subtitle = "Аккаунт user_05", TimeUtc = Now.AddMinutes(-10), Level = "success" },
                new() { Message = "Ошибка авторизации", Subtitle = "Аккаунт user_03", TimeUtc = Now.AddMinutes(-13), Level = "error" },
                new() { Message = "Мониторинг завершён", Subtitle = "10 аккаунтов", TimeUtc = Now.AddMinutes(-16), Level = "success" },
                new() { Message = "Новый отклик", Subtitle = "Аккаунт user_08", TimeUtc = Now.AddMinutes(-19), Level = "success" },
                new() { Message = "Объявление разблокировано", Subtitle = "Аккаунт user_04", TimeUtc = Now.AddMinutes(-26), Level = "success" },
                new() { Message = "Требуется авторизация", Subtitle = "Аккаунт user_03", TimeUtc = Now.AddMinutes(-32), Level = "warning" },
                new() { Message = "Heartbeat получен", Subtitle = "Агент LeadFlow", TimeUtc = Now.AddMinutes(-36), Level = "success" }
            ];
        }

        return Events
            .Where(e => e.WorkerId == workerId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(10)
            .Select(e => new DashboardEventRowViewModel
            {
                Message = e.Message,
                Subtitle = e.AccountId.HasValue ? $"Аккаунт {e.AccountId.Value.ToString()[..8]}" : (e.Details ?? string.Empty),
                TimeUtc = e.CreatedAtUtc,
                Level = e.Level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? "error"
                    : e.Level.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? "warning" : "success"
            })
            .ToList();
    }

    public static IReadOnlyList<WorkerAccountDto> GetAccounts(Guid workerId)
    {
        if (workerId == WorkerMoscowId)
        {
            return
            [
                new(AccountAlphaId, "user_01", "Active", true, 12, 0, 1, null, Now.AddMinutes(-3)),
                new(AccountBetaId, "user_02", "Active", true, 8, 1, 0, null, Now.AddMinutes(-4)),
                new(Guid.Parse("22222222-2222-2222-2222-222222222209"), "user_03", "RequiresLogin", true, 0, 0, 0, "Требуется повторный вход", Now.AddHours(-2)),
                new(Guid.Parse("22222222-2222-2222-2222-222222222210"), "user_04", "Active", true, 10, 0, 0, null, Now.AddMinutes(-8)),
                new(Guid.Parse("22222222-2222-2222-2222-222222222211"), "user_05", "Active", true, 9, 0, 0, null, Now.AddMinutes(-10)),
                new(Guid.Parse("22222222-2222-2222-2222-222222222212"), "user_06", "Active", true, 7, 0, 0, null, Now.AddMinutes(-12)),
                new(Guid.Parse("22222222-2222-2222-2222-222222222213"), "user_07", "Active", true, 11, 0, 0, null, Now.AddMinutes(-14)),
                new(Guid.Parse("22222222-2222-2222-2222-222222222214"), "user_08", "Error", true, 3, 1, 0, "Ошибка отправки в CRM", Now.AddMinutes(-16)),
                new(Guid.Parse("22222222-2222-2222-2222-222222222215"), "user_09", "Active", true, 6, 0, 0, null, Now.AddMinutes(-18)),
                new(Guid.Parse("22222222-2222-2222-2222-222222222216"), "user_10", "Active", true, 5, 0, 0, null, Now.AddMinutes(-20))
            ];
        }

        if (workerId == WorkerSpbId)
        {
            return
            [
                new(AccountGammaId, "avito_gamma", "Active", true, 15, 2, 1, null, Now.AddMinutes(-6)),
                new(Guid.Parse("22222222-2222-2222-2222-222222222205"), "avito_epsilon", "Error", true, 3, 1, 0, "Ошибка отправки в CRM", Now.AddMinutes(-12)),
                new(Guid.Parse("22222222-2222-2222-2222-222222222206"), "avito_zeta", "Paused", false, 0, 0, 0, null, Now.AddDays(-1))
            ];
        }

        if (workerId == WorkerKazanId)
        {
            return
            [
                new(Guid.Parse("22222222-2222-2222-2222-222222222207"), "avito_eta", "Offline", false, 0, 0, 0, null, Now.AddMinutes(-18)),
                new(Guid.Parse("22222222-2222-2222-2222-222222222208"), "avito_theta", "Offline", false, 0, 0, 0, null, Now.AddMinutes(-18))
            ];
        }

        return [];
    }

    public static IReadOnlyList<WorkerEventListItem> Events =>
    [
        new(Guid.Parse("33333333-3333-3333-3333-333333333301"), WorkerMoscowId, "VDS-Москва-01", AccountAlphaId, "Info", "Отклик отправлен в CRM", null, Now.AddMinutes(-1)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333302"), WorkerSpbId, "VDS-СПб-02", AccountGammaId, "Warning", "Дубликат отклика пропущен", "candidate_id=88421", Now.AddMinutes(-4)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333303"), WorkerMoscowId, "VDS-Москва-01", AccountBetaId, "Info", "Мониторинг завершён", "3 аккаунта", Now.AddMinutes(-7)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333304"), WorkerSpbId, "VDS-СПб-02", Guid.Parse("22222222-2222-2222-2222-222222222205"), "Error", "Ошибка отправки в CRM", "HTTP 503", Now.AddMinutes(-12)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333305"), WorkerKazanId, "VDS-Казань-03", null, "Warning", "Heartbeat не получен", "18 мин", Now.AddMinutes(-18)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333306"), WorkerMoscowId, "VDS-Москва-01", AccountAlphaId, "Info", "Новый отклик получен", "vacancy_id=120984", Now.AddMinutes(-22)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333307"), WorkerSpbId, "VDS-СПб-02", AccountGammaId, "Info", "Баланс обновлён", "67400 ₽", Now.AddMinutes(-35)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333308"), WorkerMoscowId, "VDS-Москва-01", Guid.Parse("22222222-2222-2222-2222-222222222204"), "Warning", "Требуется авторизация", null, Now.AddHours(-2))
    ];

    private static IReadOnlyList<ActivityPointDto> BuildHourly()
    {
        var profile = HourlyResponsesGenerator.DailyValues;
        var points = new List<ActivityPointDto>(profile.Count);
        for (var h = 0; h < profile.Count; h++)
        {
            var value = profile[h];
            points.Add(new ActivityPointDto(
                $"{h:00}:00",
                value,
                Math.Max(0, value - 2),
                h == 11 ? 2 : 0,
                h == 14 ? 1 : 0,
                h,
                1,
                DateTime.Today.AddHours(Math.Min(h, 23))));
        }

        return points;
    }

    private static IReadOnlyList<ActivityPointDto> BuildWeekly()
    {
        var days = new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
        var counts = new[] { 142, 168, 155, 184, 176, 98, 64 };
        return days.Select((label, i) => new ActivityPointDto(
            label,
            counts[i],
            counts[i] - 10,
            4,
            i == 3 ? 3 : 1,
            0,
            24,
            DateTime.Today.AddDays(-6 + i))).ToList();
    }

    public static ErrorsIndexViewModel BuildErrorsIndexViewModel(
        ErrorsFilterViewModel filters,
        int page,
        int pageSize) =>
        ErrorsIndexBuilder.Build(BuildPreviewErrorRows(), filters, page, pageSize);

    private static IReadOnlyList<ErrorRowViewModel> BuildPreviewErrorRows()
    {
        const int total = 256;
        var rng = new Random(5150);
        var rows = new List<ErrorRowViewModel>(total);
        var severityPlan = new List<string>(total);

        severityPlan.AddRange(Enumerable.Repeat("critical", 20));
        severityPlan.AddRange(Enumerable.Repeat("high", 56));
        severityPlan.AddRange(Enumerable.Repeat("medium", 115));
        severityPlan.AddRange(Enumerable.Repeat("low", 65));

        for (var i = severityPlan.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (severityPlan[i], severityPlan[j]) = (severityPlan[j], severityPlan[i]);
        }

        var types = new[] { "auth", "bitrix", "network", "balance", "blocked", "parsing", "postgres", "api", "unknown" };
        var messagesByType = new Dictionary<string, string[]>
        {
            ["auth"] = ["Неверный логин или пароль", "Сессия аккаунта истекла", "Требуется повторная авторизация"],
            ["bitrix"] = ["Ошибка отправки сообщения в Bitrix24", "CRM вернула код 503", "Не удалось создать лид в Bitrix24"],
            ["network"] = ["Таймаут соединения с API", "Сеть недоступна", "Ошибка DNS при обращении к серверу"],
            ["balance"] = ["Не удалось получить баланс аккаунта", "Ошибка обновления баланса"],
            ["blocked"] = ["Аккаунт заблокирован на Avito", "Объявления аккаунта заблокированы"],
            ["parsing"] = ["Ошибка парсинга ответа Avito", "Некорректный формат HTML-страницы"],
            ["postgres"] = ["Не удалось подключиться к PostgreSQL", "Ошибка записи в базу данных"],
            ["api"] = ["API вернул код 500", "Недопустимый ответ API"],
            ["unknown"] = ["Неизвестная ошибка обработки", "Непредвиденное исключение в агенте"]
        };

        for (var i = 0; i < total; i++)
        {
            var severity = severityPlan[i];
            var errorType = types[i % types.Length];
            var workerIndex = i % PreviewWorkerIds.Length;
            var workerId = PreviewWorkerIds[workerIndex];
            var workerName = $"Worker #{workerIndex + 1}";
            var accountName = $"user_{(i % 120) + 1:D2}";
            var accountId = Guid.Parse($"33333333-3333-3333-3333-{(i % 120) + 1:D12}");
            var messages = messagesByType[errorType];
            var message = messages[i % messages.Length];
            var firstSeenMinutes = 180 + i * 7 + rng.Next(0, 20);
            var lastSeenMinutes = rng.Next(1, Math.Max(2, firstSeenMinutes / 3));
            var occurredAt = Now.AddMinutes(-firstSeenMinutes);
            var lastSeen = Now.AddMinutes(-lastSeenMinutes);
            var occurrences = severity switch
            {
                "critical" => rng.Next(12, 150),
                "high" => rng.Next(5, 80),
                "medium" => rng.Next(2, 40),
                _ => rng.Next(1, 15)
            };

            rows.Add(new ErrorRowViewModel
            {
                Id = Guid.Parse($"55555555-5555-5555-5555-{(i + 1):D12}"),
                OccurredAtUtc = occurredAt,
                Severity = severity,
                SeverityLabel = ErrorsIndexBuilder.SeverityLabel(severity),
                ErrorType = errorType,
                ErrorTypeLabel = ErrorsIndexBuilder.ErrorTypeLabel(errorType),
                Message = message,
                CopyText = message,
                AccountName = accountName,
                AccountId = accountId,
                WorkerId = workerId,
                WorkerName = workerName,
                OccurrenceCount = occurrences,
                LastSeenUtc = lastSeen
            });
        }

        return rows;
    }

    public static EventsIndexViewModel BuildEventsIndexViewModel(
        EventsFilterViewModel filters,
        int page,
        int pageSize) =>
        EventsIndexBuilder.Build(
            BuildPreviewEventRows(),
            filters,
            page,
            pageSize: pageSize);

    private static IReadOnlyList<EventRowViewModel> BuildPreviewEventRows()
    {
        const int total = 1254;
        var rng = new Random(9091);
        var rows = new List<EventRowViewModel>(total);
        var levelPlan = new List<string>(total);

        levelPlan.AddRange(Enumerable.Repeat("success", 752));
        levelPlan.AddRange(Enumerable.Repeat("info", 251));
        levelPlan.AddRange(Enumerable.Repeat("warning", 150));
        levelPlan.AddRange(Enumerable.Repeat("error", 101));

        for (var i = levelPlan.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (levelPlan[i], levelPlan[j]) = (levelPlan[j], levelPlan[i]);
        }

        var responseMessages = new[]
        {
            "Получен новый отклик на объявление №{0}",
            "Новый отклик отправлен в CRM",
            "Отклик успешно обработан"
        };
        var duplicateMessages = new[]
        {
            "Найден дубликат отклика",
            "Дубликат отклика пропущен"
        };
        var errorMessages = new[]
        {
            "Ошибка отправки в CRM",
            "Ошибка авторизации аккаунта",
            "Ошибка при отправке в Bitrix24"
        };
        var authMessages = new[]
        {
            "Аккаунт успешно авторизован",
            "Требуется повторная авторизация"
        };
        var balanceMessages = new[]
        {
            "Баланс обновлён",
            "Обновление баланса завершено"
        };
        var startMessages = new[] { "Воркер запущен", "Мониторинг запущен" };
        var stopMessages = new[] { "Heartbeat не получен", "Воркер остановлен" };
        var infoMessages = new[] { "Мониторинг завершён", "Плановая проверка выполнена", "Конфигурация обновлена" };

        for (var i = 0; i < total; i++)
        {
            var level = levelPlan[i];
            var workerIndex = i % PreviewWorkerIds.Length;
            var workerId = PreviewWorkerIds[workerIndex];
            var workerName = $"Worker #{workerIndex + 1}";
            var accountName = $"user_{(i % 120) + 1:D2}";
            var accountId = Guid.Parse($"33333333-3333-3333-3333-{(i % 120) + 1:D12}");
            var minutesAgo = i * 3 + rng.Next(0, 5);
            var occurredAt = Now.AddMinutes(-minutesAgo);

            string message;
            string? details = null;
            string type;

            if (level == "warning" && i % 2 == 0)
            {
                message = duplicateMessages[i % duplicateMessages.Length];
                details = $"candidate_id={rng.Next(10000, 99999)}";
                type = "duplicate";
            }
            else if (level == "error")
            {
                message = errorMessages[i % errorMessages.Length];
                details = i % 2 == 0 ? "HTTP 503" : null;
                type = "error";
            }
            else if (level == "success" && i % 3 == 0)
            {
                message = string.Format(responseMessages[i % responseMessages.Length], rng.Next(10000000, 99999999));
                type = "response";
            }
            else if (i % 17 == 0)
            {
                message = authMessages[i % authMessages.Length];
                type = level == "warning" ? "auth" : "auth";
                if (level == "warning") level = "warning";
            }
            else if (i % 19 == 0)
            {
                message = balanceMessages[i % balanceMessages.Length];
                details = $"{rng.Next(5, 98) * 1000} ₽";
                type = "balance";
                level = "info";
            }
            else if (i % 23 == 0)
            {
                message = startMessages[i % startMessages.Length];
                type = "start";
            }
            else if (i % 29 == 0)
            {
                message = stopMessages[i % stopMessages.Length];
                type = "stop";
                level = "warning";
            }
            else
            {
                message = level == "success"
                    ? responseMessages[i % responseMessages.Length].Contains('{')
                        ? string.Format(responseMessages[i % responseMessages.Length], rng.Next(10000000, 99999999))
                        : responseMessages[i % responseMessages.Length]
                    : infoMessages[i % infoMessages.Length];
                type = level == "success" ? "response" : "info";
            }

            var (typeLabel, typeIcon, typeTone) = EventsIndexBuilder.EventTypePresentation(type);
            var description = details is null ? message : $"{message} — {details}";

            rows.Add(new EventRowViewModel
            {
                Id = Guid.Parse($"44444444-4444-4444-4444-{(i + 1):D12}"),
                OccurredAtUtc = occurredAt,
                EventType = type,
                EventTypeLabel = typeLabel,
                EventTypeIcon = typeIcon,
                EventTypeTone = typeTone,
                Level = level,
                LevelLabel = level switch
                {
                    "error" => "Ошибка",
                    "warning" => "Предупреждение",
                    "info" => "Информация",
                    _ => "Успех"
                },
                AccountName = accountName,
                AccountId = accountId,
                WorkerId = workerId,
                WorkerName = workerName,
                Description = description,
                CopyText = description
            });
        }

        return rows;
    }

    public static AccountsIndexViewModel BuildAccountsIndexViewModel(
        string? searchQuery,
        string? tab,
        int page,
        int pageSize) =>
        AccountsIndexBuilder.Build(BuildPreviewAccountRows(), searchQuery, tab, page, pageSize);

    private static IReadOnlyList<AccountRowViewModel> BuildPreviewAccountRows()
    {
        const int total = 120;
        var rng = new Random(4242);
        var rows = new List<AccountRowViewModel>(total);

        for (var i = 1; i <= total; i++)
        {
            var workerIndex = (i - 1) % PreviewWorkerIds.Length;
            var workerId = PreviewWorkerIds[workerIndex];
            var workerName = $"Worker #{workerIndex + 1}";

            string label;
            string tone;
            if (i <= 98)
            {
                label = "Активен";
                tone = "active";
            }
            else if (i <= 110)
            {
                label = "Неактивен";
                tone = "inactive";
            }
            else if (i <= 116)
            {
                label = "Заблокирован";
                tone = "blocked";
            }
            else
            {
                label = "Ошибка";
                tone = "error";
            }

            var responses = tone == "inactive" ? 0 : rng.Next(8, 64);
            var errors = tone == "error" ? rng.Next(1, 5) : tone == "active" ? rng.Next(0, 2) : 0;
            var unique = Math.Max(0, responses - rng.Next(0, Math.Max(1, responses / 4)));
            var balance = tone is "inactive" or "blocked" ? 0m : rng.Next(400, 9800) * 10m + rng.Next(0, 9) * 100m + 50m;
            var lastActivity = tone switch
            {
                "inactive" => (DateTime?)null,
                "blocked" => Now.AddDays(-rng.Next(2, 14)),
                "error" => Now.AddMinutes(-rng.Next(30, 240)),
                _ => Now.AddMinutes(-rng.Next(1, 180))
            };

            rows.Add(new AccountRowViewModel
            {
                Id = Guid.Parse($"33333333-3333-3333-3333-{i:D12}"),
                AccountName = $"user_{i:D2}",
                WorkerId = workerId,
                WorkerName = workerName,
                StatusLabel = label,
                StatusTone = tone,
                Balance = balance,
                Responses = responses,
                UniqueResponses = unique,
                Errors = errors,
                LastActivityUtc = lastActivity
            });
        }

        return rows;
    }

    public static IReadOnlyList<PanelUserDto> PanelUsers =>
    [
        new("preview-admin", "admin@orbita.local", true, PanelRoles.Admin, false),
        new("preview-operator", "operator@orbita.local", true, PanelRoles.Operator, true)
    ];

    public static PanelProfileDto PanelProfile =>
        new("admin@orbita.local", PanelRoles.Admin);

    public static PasswordPolicyDto PasswordPolicy =>
        new(8, true, false, false, false, 1);

    public static WorkerRegistrationInfoDto WorkerRegistrationInfo =>
        new(true, "****demo", "config");

    public static IReadOnlyList<AdminWorkerListItemDto> AdminWorkers =>
    [
        new(WorkerMoscowId, "Москва-01", "WIN-M01", "1.0.0", true, true, Now.AddMinutes(-2), Now.AddDays(-14), Now.AddDays(-3)),
        new(WorkerSpbId, "СПб-02", "WIN-SPB02", "1.0.0", true, false, Now.AddHours(-2), Now.AddDays(-10), null),
        new(WorkerKazanId, "Казань-03", "WIN-KZN03", "0.9.5", false, false, Now.AddDays(-1), Now.AddDays(-30), Now.AddDays(-7))
    ];

    public static PanelAuditPageDto BuildPanelAuditPage(
        string? q,
        string? action,
        DateTime? date,
        int page,
        int pageSize = 50)
    {
        IEnumerable<PanelAuditEntryDto> rows =
        [
            new(1, Now.AddMinutes(-5), "admin@orbita.local", PanelAuditActions.LoginSucceeded, "user", "preview-admin", null, "127.0.0.1"),
            new(2, Now.AddMinutes(-18), "admin@orbita.local", PanelAuditActions.WorkerKeyRotated, "worker", WorkerMoscowId.ToString(), null, "127.0.0.1"),
            new(3, Now.AddHours(-1), "admin@orbita.local", PanelAuditActions.UserLocked, "user", "preview-operator", "operator@orbita.local", "127.0.0.1"),
            new(4, Now.AddHours(-3), null, PanelAuditActions.LoginFailed, "user", null, "invalid_password", "10.0.0.5")
        ];

        if (!string.IsNullOrWhiteSpace(action))
        {
            rows = rows.Where(r => string.Equals(r.Action, action, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var query = q.Trim();
            rows = rows.Where(r =>
                (r.ActorEmail?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Details?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || r.Action.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var items = rows.ToList();
        var total = items.Count;
        var paged = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new PanelAuditPageDto(paged, total, page, pageSize);
    }

    public static ServiceLogsPageDto BuildServiceLogsPage(
        string? q,
        string? level,
        string? service,
        DateTime? date,
        int page,
        int pageSize = 50)
    {
        IEnumerable<ServiceLogEntryDto> rows =
        [
            new(Now.AddMinutes(-3), "Info", "Orbita.Web", "[SettingsService.GetIndexAsync]", "Settings page opened (users tab). Session validated, cached profile loaded, rendering 12 panel users with 2 pending role updates.", null, false),
            new(Now.AddMinutes(-12), "Warning", "Orbita.Api", "[Program.Login]", "Login failed: invalid password for demo@orbita.local from 192.168.1.44. Attempt 3 of 5 before temporary lockout.", null, false),
            new(Now.AddMinutes(-28), "Error", "Orbita.Api", "[TelemetryService.HeartbeatAsync]", "Worker heartbeat timeout for WIN-W03 after 30s. LastSeenAtUtc=2026-06-27T08:41:12Z, expected interval=15s. Scheduling retry 2/3 and marking worker as offline in dashboard cache.", "trace-demo-001", false),
            new(Now.AddMinutes(-45), "Error", "Orbita.Api", "[WorkerAdminService.RotateKeyAsync]", "Failed to rotate worker API key: database connection timeout after 30s.\nWorkerId=8f2c1a9b-4d3e-4f5a-9b0c-1d2e3f4a5b6c\nMachine=WIN-W03\nRetry scheduled in 60s.\nSystem.TimeoutException: Timeout during reading from stream\n   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(...)\n   at Orbita.Api.Services.WorkerAdminService.RotateKeyAsync(...)", "trace-demo-002", false),
            new(Now.AddHours(-1), "Info", "Orbita.Web", "[DashboardService.GetIndexAsync]", "Dashboard summary loaded: 4 workers online, 128 active leads, 3 errors in the last hour.", null, false),
            new(Now.AddHours(-2), "Debug", "Orbita.Api", "[ServiceLogsQueryService.SearchAsync]", "Service logs query completed in 42ms. Filters: level=(all), service=(all), date=today, q=(empty), page=1, pageSize=50, total=6.", null, false)
        ];

        if (!string.IsNullOrWhiteSpace(level))
        {
            rows = rows.Where(r => string.Equals(r.Level, level, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(service))
        {
            rows = rows.Where(r => string.Equals(r.Service, service, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            rows = rows.Where(r =>
                r.Message.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Source.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (r.TraceId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = rows.ToList();
        var items = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new ServiceLogsPageDto(items, list.Count, page, pageSize);
    }
}
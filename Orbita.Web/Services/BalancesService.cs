using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public sealed class BalancesService(
    OrbitaApiClient api,
    IOfficeContext officeContext) : IBalancesService
{
    public async Task<BalancesIndexViewModel> GetIndexAsync(
        string? tab = null,
        string? query = null,
        bool history = false,
        int page = 1,
        IReadOnlyList<Guid>? excludedWorkerIds = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        var accountsTask = api.GetOfficeBalancesAsync(ct: ct);
        var sessionsTask = api.GetTopUpSessionsAsync(history, ct);
        await Task.WhenAll(accountsTask, sessionsTask);

        var accounts = await accountsTask ?? [];
        var workerOptions = accounts
            .GroupBy(x => x.WorkerId)
            .Select(group => new EventFilterOptionViewModel
            {
                Value = group.Key.ToString(),
                Label = group.First().WorkerDisplayName
            })
            .OrderBy(x => x.Label)
            .ToArray();
        var excludedIds = (excludedWorkerIds ?? [])
            .Where(id => workerOptions.Any(option => option.Value == id.ToString()))
            .Distinct()
            .ToArray();
        var excludedWorkers = excludedIds.ToHashSet();
        var scopedAccounts = excludedIds.Length == 0
            ? accounts
            : accounts.Where(x => !excludedWorkers.Contains(x.WorkerId)).ToArray();
        var sessions = excludedIds.Length == 0
            ? await sessionsTask
            : (await sessionsTask).Where(x => !excludedWorkers.Contains(x.WorkerId)).ToArray();
        var byKey = sessions
            .Where(x => !string.IsNullOrWhiteSpace(x.SubProfileId))
            .GroupBy(x => (x.WorkerId, x.AccountId, x.SubProfileId), EqualityComparer<(Guid, Guid, string)>.Default)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(s => s.CreatedAtUtc).First());

        var allRows = new List<BalanceSubProfileRowViewModel>();
        foreach (var item in scopedAccounts)
        {
            var profiles = item.SubProfiles ?? [];
            for (var i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                if (profile.Balance is not decimal current)
                {
                    continue;
                }

                var session = byKey.GetValueOrDefault((item.WorkerId, item.AccountId, profile.Id));
                var target = TopUpSessionRules.ResolveTargetBalance(profile.TodayResponses);
                var row = new BalanceSubProfileRowViewModel
                {
                    WorkerId = item.WorkerId,
                    AccountId = item.AccountId,
                    WorkerName = item.WorkerDisplayName,
                    AccountName = item.AccountName,
                    SubProfileId = profile.Id,
                    SubProfileName = profile.Name,
                    Balance = current,
                    RecommendedTarget = target,
                    RecommendedAmount = Math.Max(0m, target - current),
                    TodayResponses = profile.TodayResponses,
                    WorkerOnline = item.WorkerIsOnline,
                    IsLowBalance = current < TopUpSessionRules.LowBalanceThresholdRub,
                    LastUpdatedAtUtc = item.LastMonitoringAtUtc,
                    IsStale = item.LastMonitoringAtUtc is null,
                    Session = session
                };

                allRows.Add(row);
            }
        }

        var filteredRows = allRows
            .Where(x => Matches(x, tab, query, history))
            .OrderBy(x => x.Balance)
            .ThenBy(x => x.AccountName)
            .ThenBy(x => x.SubProfileName)
            .ToList();
        const int pageSize = 50;
        var rows = filteredRows.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var today = DateTime.UtcNow.Date;
        var historySessions = sessions
            .OrderByDescending(x => x.CompletedAtUtc ?? x.CreatedAtUtc)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ToArray();
        return new BalancesIndexViewModel
        {
            Header = PageHeaderBuilder.WithOfficeScope(
                PageHeaderBuilder.Create("Балансы", "Контроль балансов и пополнение аккаунтов Avito"),
                officeContext),
            KpiCards = BuildKpiCards(allRows, sessions, today),
            Rows = rows,
            Sessions = history
                ? historySessions.Skip((page - 1) * pageSize).Take(pageSize).ToArray()
                : sessions,
            Workers = workerOptions,
            ExcludedWorkerIds = excludedIds,
            Pagination = new PaginationViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalItems = history ? sessions.Count : filteredRows.Count
            },
            TotalSubProfiles = allRows.Count,
            LowBalanceCount = allRows.Count(x => x.IsLowBalance && x.Session is null),
            QueueCount = sessions.Count(x => x.Status is TopUpSessionStatuses.Requested or TopUpSessionStatuses.Started or TopUpSessionStatuses.PaymentClaimed or TopUpSessionStatuses.QrReady),
            AwaitingBalanceCount = sessions.Count(x => x.Status is TopUpSessionStatuses.AwaitingBalance or TopUpSessionStatuses.VerificationRequired),
            CompletedTodayCount = sessions.Count(x => x.Status is TopUpSessionStatuses.Completed && x.CompletedAtUtc?.ToUniversalTime().Date == today)
            ,
            TotalBalance = allRows.Sum(x => x.Balance),
            SelectableCount = allRows.Count(x => x.IsLowBalance && x.WorkerOnline && x.Session is null),
            OfflineLowBalanceCount = allRows.Count(x => x.IsLowBalance && !x.WorkerOnline && x.Session is null)
        };
    }

    private static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(
        List<BalanceSubProfileRowViewModel> rows,
        IReadOnlyList<TopUpSessionDto> sessions,
        DateTime today)
    {
        var totalBalance = rows.Sum(x => x.Balance);
        var lowBalanceCount = rows.Count(x => x.IsLowBalance && x.Session is null);
        var queueCount = sessions.Count(x => x.Status is TopUpSessionStatuses.Requested or TopUpSessionStatuses.Started or TopUpSessionStatuses.PaymentClaimed or TopUpSessionStatuses.QrReady);
        var awaitingCount = sessions.Count(x => x.Status is TopUpSessionStatuses.AwaitingBalance or TopUpSessionStatuses.VerificationRequired);
        var completedToday = sessions.Count(x => x.Status is TopUpSessionStatuses.Completed && x.CompletedAtUtc?.ToUniversalTime().Date == today);

        return
        [
            new()
            {
                Key = "total_balance",
                Href = "/Balances?tab=all",
                Label = "Общий баланс",
                Value = $"{totalBalance:N0} ₽",
                CountValue = (double)totalBalance,
                ValueSuffix = " ₽",
                Delta = "по субпрофилям с известными данными",
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-wallet",
                IconTone = "blue"
            },
            new()
            {
                Key = "subprofiles",
                Href = "/Balances?tab=all",
                Label = "Всего субпрофилей",
                Value = rows.Count.ToString(),
                CountValue = rows.Count,
                Delta = "с известным балансом",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-user",
                IconTone = "gray"
            },
            new()
            {
                Key = "low_balance",
                Href = "/Balances?tab=low",
                Label = "Низкий баланс",
                Value = lowBalanceCount.ToString(),
                CountValue = lowBalanceCount,
                Delta = "ниже 150 ₽",
                DeltaTone = lowBalanceCount > 0 ? "bad" : "good",
                IconClass = "fa-solid fa-triangle-exclamation",
                IconTone = "orange"
            },
            new()
            {
                Key = "queue",
                Href = "/Balances?tab=queue",
                Label = "В очереди",
                Value = queueCount.ToString(),
                CountValue = queueCount,
                Delta = "запросов пополнения",
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-clock",
                IconTone = "blue"
            },
            new()
            {
                Key = "awaiting",
                Href = "/Balances?tab=awaiting",
                Label = "Ожидают баланс",
                Value = awaitingCount.ToString(),
                CountValue = awaitingCount,
                Delta = "требуют внимания",
                DeltaTone = awaitingCount > 0 ? "bad" : "good",
                IconClass = "fa-solid fa-hourglass-half",
                IconTone = "orange"
            },
            new()
            {
                Key = "completed_today",
                Href = "/Balances?tab=history&history=true",
                Label = "Пополнено сегодня",
                Value = completedToday.ToString(),
                CountValue = completedToday,
                Delta = "подтверждено",
                DeltaTone = "good",
                IconClass = "fa-solid fa-circle-check",
                IconTone = "green"
            }
        ];
    }

    private static bool Matches(
        BalanceSubProfileRowViewModel row,
        string? tab,
        string? query,
        bool history)
    {
        if (!string.IsNullOrWhiteSpace(query)
            && !string.Join(' ', row.WorkerName, row.AccountName, row.SubProfileName)
                .Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return tab switch
        {
            "all" => true,
            "queue" => row.Session is not null && row.Session.Status is TopUpSessionStatuses.Requested or TopUpSessionStatuses.Started,
            "working" => row.Session is not null && row.Session.Status is TopUpSessionStatuses.PaymentClaimed or TopUpSessionStatuses.QrReady,
            "awaiting" => row.Session is not null && row.Session.Status is TopUpSessionStatuses.AwaitingBalance or TopUpSessionStatuses.VerificationRequired,
            "history" or _ when history => row.Session is not null,
            "low" or null or "" => row.IsLowBalance && row.Session is null,
            _ => row.IsLowBalance
        };
    }
}

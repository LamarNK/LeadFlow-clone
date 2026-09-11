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
        CancellationToken ct = default)
    {
        var accountsTask = api.GetOfficeAccountsAsync(ct: ct);
        var sessionsTask = api.GetTopUpSessionsAsync(history, ct);
        await Task.WhenAll(accountsTask, sessionsTask);

        var accounts = await accountsTask ?? [];
        var sessions = await sessionsTask;
        var byKey = sessions
            .Where(x => !string.IsNullOrWhiteSpace(x.SubProfileId))
            .GroupBy(x => (x.WorkerId, x.AccountId, x.SubProfileId), EqualityComparer<(Guid, Guid, string)>.Default)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(s => s.CreatedAtUtc).First());

        var allRows = new List<BalanceSubProfileRowViewModel>();
        foreach (var item in accounts)
        {
            var profiles = item.Account.SubProfiles ?? [];
            var balances = item.Balance?.SubProfiles ?? [];
            for (var i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                var balance = ResolveBalance(profile, balances, i);
                if (balance is not decimal current)
                {
                    continue;
                }

                var session = byKey.GetValueOrDefault((item.WorkerId, item.Account.AccountId, profile.Id));
                var target = TopUpSessionRules.ResolveTargetBalance(profile.TodayResponses);
                var row = new BalanceSubProfileRowViewModel
                {
                    WorkerId = item.WorkerId,
                    AccountId = item.Account.AccountId,
                    WorkerName = item.WorkerDisplayName,
                    AccountName = item.Account.DisplayName,
                    SubProfileId = profile.Id,
                    SubProfileName = profile.Name,
                    Balance = current,
                    RecommendedTarget = target,
                    RecommendedAmount = Math.Max(0m, target - current),
                    TodayResponses = profile.TodayResponses,
                    WorkerOnline = item.WorkerIsOnline,
                    IsLowBalance = current < TopUpSessionRules.LowBalanceThresholdRub,
                    LastUpdatedAtUtc = item.Account.LastMonitoringAt,
                    IsStale = item.Account.LastMonitoringAt is null
                              || item.Account.LastMonitoringAt < DateTime.UtcNow.AddMinutes(-30),
                    Session = session
                };

                allRows.Add(row);
            }
        }

        var rows = allRows
            .Where(x => Matches(x, tab, query, history))
            .OrderBy(x => x.Balance)
            .ThenBy(x => x.AccountName)
            .ThenBy(x => x.SubProfileName)
            .ToList();

        var today = DateTime.UtcNow.Date;
        return new BalancesIndexViewModel
        {
            Header = PageHeaderBuilder.WithOfficeScope(
                PageHeaderBuilder.Create("Балансы", "Контроль балансов и пополнение аккаунтов Avito"),
                officeContext),
            Rows = rows,
            Sessions = sessions,
            TotalSubProfiles = allRows.Count,
            LowBalanceCount = allRows.Count(x => x.IsLowBalance && x.Session is null),
            QueueCount = sessions.Count(x => x.Status is TopUpSessionStatuses.Requested or TopUpSessionStatuses.Started or TopUpSessionStatuses.PaymentClaimed or TopUpSessionStatuses.QrReady),
            AwaitingBalanceCount = sessions.Count(x => x.Status is TopUpSessionStatuses.AwaitingBalance or TopUpSessionStatuses.VerificationRequired),
            CompletedTodayCount = sessions.Count(x => x.Status is TopUpSessionStatuses.Completed && x.CompletedAtUtc?.ToUniversalTime().Date == today)
        };
    }

    private static decimal? ResolveBalance(
        WorkerSubProfileDto profile,
        IReadOnlyList<SubProfileBalanceDto> balances,
        int index)
    {
        if (profile.Balance is decimal value)
        {
            return value;
        }
        if (index < balances.Count)
        {
            return balances[index].Balance;
        }
        return balances.FirstOrDefault(x =>
            string.Equals(x.SubProfileName, profile.Name, StringComparison.OrdinalIgnoreCase))?.Balance;
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

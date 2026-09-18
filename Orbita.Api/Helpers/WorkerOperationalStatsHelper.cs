using Orbita.Contracts;

namespace Orbita.Api.Helpers;

internal sealed record WorkerOperationalStats(
    int TodayResponses,
    int TodayDuplicates,
    int TodayEventErrors,
    int ActiveAccounts,
    int TotalAccounts,
    int LowBalanceAccountCount,
    IReadOnlyList<DashboardWorkerAccountItem> Accounts,
    decimal TotalBalance)
{
    public static WorkerOperationalStats Empty { get; } = new(0, 0, 0, 0, 0, 0, [], 0);
}

internal static class WorkerOperationalStatsHelper
{
    public static Dictionary<Guid, WorkerOperationalStats> Merge(
        IReadOnlyList<Guid> workerIds,
        IReadOnlyDictionary<Guid, (int Total, int Duplicates, int ResponseErrors)> responseStats,
        IReadOnlyDictionary<Guid, int> eventErrors,
        IReadOnlyDictionary<Guid, (int Total, int Active)> accountStats,
        IReadOnlyDictionary<Guid, int> lowBalanceAccountCounts,
        IReadOnlyDictionary<Guid, IReadOnlyList<DashboardWorkerAccountItem>> workerAccounts,
        IReadOnlyDictionary<Guid, decimal> balances)
    {
        var result = new Dictionary<Guid, WorkerOperationalStats>();
        foreach (var workerId in workerIds)
        {
            responseStats.TryGetValue(workerId, out var responses);
            eventErrors.TryGetValue(workerId, out var errors);
            accountStats.TryGetValue(workerId, out var accounts);
            lowBalanceAccountCounts.TryGetValue(workerId, out var lowBalanceCount);
            workerAccounts.TryGetValue(workerId, out var accountsForWorker);
            balances.TryGetValue(workerId, out var totalBalance);

            result[workerId] = new WorkerOperationalStats(
                responses.Total,
                responses.Duplicates,
                errors,
                accounts.Active,
                accounts.Total,
                lowBalanceCount,
                accountsForWorker ?? [],
                totalBalance);
        }

        return result;
    }
}

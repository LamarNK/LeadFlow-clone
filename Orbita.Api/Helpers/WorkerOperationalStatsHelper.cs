namespace Orbita.Api.Helpers;

internal sealed record WorkerOperationalStats(
    int TodayResponses,
    int TodayDuplicates,
    int TodayEventErrors,
    int ActiveAccounts,
    int TotalAccounts)
{
    public static WorkerOperationalStats Empty { get; } = new(0, 0, 0, 0, 0);
}

internal static class WorkerOperationalStatsHelper
{
    public static Dictionary<Guid, WorkerOperationalStats> Merge(
        IReadOnlyList<Guid> workerIds,
        IReadOnlyDictionary<Guid, (int Total, int Duplicates, int ResponseErrors)> responseStats,
        IReadOnlyDictionary<Guid, int> eventErrors,
        IReadOnlyDictionary<Guid, (int Total, int Active)> accountStats)
    {
        var result = new Dictionary<Guid, WorkerOperationalStats>();
        foreach (var workerId in workerIds)
        {
            responseStats.TryGetValue(workerId, out var responses);
            eventErrors.TryGetValue(workerId, out var errors);
            accountStats.TryGetValue(workerId, out var accounts);

            result[workerId] = new WorkerOperationalStats(
                responses.Total,
                responses.Duplicates,
                errors,
                accounts.Active,
                accounts.Total);
        }

        return result;
    }
}
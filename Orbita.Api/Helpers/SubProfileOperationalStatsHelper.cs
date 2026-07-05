using Orbita.Contracts;

namespace Orbita.Api.Helpers;

internal sealed record SubProfileOperationalStats(
    int TodayResponses,
    int TodayDuplicates,
    int TodayEventErrors,
    DateTime? LastActivityUtc);

internal static class SubProfileOperationalStatsHelper
{
    public static Dictionary<(Guid AccountId, string SubProfileId), SubProfileOperationalStats> MergeResponseStats(
        IEnumerable<(Guid AccountId, string SubProfileId, int Total, int Duplicates, int Errors, DateTime? LastActivity)> rows)
    {
        var result = new Dictionary<(Guid, string), SubProfileOperationalStats>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.SubProfileId))
            {
                continue;
            }

            var key = (row.AccountId, row.SubProfileId.Trim());
            result[key] = new SubProfileOperationalStats(
                row.Total,
                row.Duplicates,
                row.Errors,
                row.LastActivity);
        }

        return result;
    }

    public static void ApplyEventErrors(
        Dictionary<(Guid AccountId, string SubProfileId), SubProfileOperationalStats> stats,
        IEnumerable<(Guid AccountId, string? Details)> eventRows,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> subProfileIdsByAccount)
    {
        foreach (var (accountId, details) in eventRows)
        {
            if (!subProfileIdsByAccount.TryGetValue(accountId, out var lookup))
            {
                continue;
            }

            var subProfileId = WorkerEventDetailsParser.TryParseDiagnosticSubProfileId(details);
            if (string.IsNullOrWhiteSpace(subProfileId))
            {
                var subProfileName = WorkerEventDetailsParser.TryParseDiagnosticSubProfileName(details);
                if (string.IsNullOrWhiteSpace(subProfileName))
                {
                    continue;
                }

                subProfileId = lookup.FirstOrDefault(x =>
                    string.Equals(x.Value, subProfileName, StringComparison.OrdinalIgnoreCase)).Key;
            }

            if (string.IsNullOrWhiteSpace(subProfileId))
            {
                continue;
            }

            var key = (accountId, subProfileId.Trim());
            if (stats.TryGetValue(key, out var existing))
            {
                stats[key] = existing with { TodayEventErrors = existing.TodayEventErrors + 1 };
            }
            else
            {
                stats[key] = new SubProfileOperationalStats(0, 0, 1, null);
            }
        }
    }

    public static IReadOnlyList<WorkerSubProfileDto>? Enrich(
        IReadOnlyList<WorkerSubProfileDto>? profiles,
        Guid accountId,
        IReadOnlyDictionary<(Guid AccountId, string SubProfileId), SubProfileOperationalStats> stats)
    {
        if (profiles is null || profiles.Count == 0)
        {
            return profiles;
        }

        return profiles
            .Select(profile =>
            {
                stats.TryGetValue((accountId, profile.Id), out var operational);
                operational ??= new SubProfileOperationalStats(0, 0, 0, null);
                var lastActivity = operational.LastActivityUtc ?? profile.LastIssueAt;
                return profile with
                {
                    TodayResponses = operational.TodayResponses,
                    TodayDuplicates = operational.TodayDuplicates,
                    TodayEventErrors = operational.TodayEventErrors,
                    LastActivityUtc = lastActivity
                };
            })
            .ToList();
    }

    public static IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> BuildNameLookup(
        IEnumerable<(Guid AccountId, IReadOnlyList<WorkerSubProfileDto>? Profiles)> accounts)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        foreach (var (accountId, profiles) in accounts)
        {
            if (profiles is null || profiles.Count == 0)
            {
                continue;
            }

            result[accountId] = profiles
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .ToDictionary(
                    p => p.Id,
                    p => string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name,
                    StringComparer.Ordinal);
        }

        return result;
    }
}
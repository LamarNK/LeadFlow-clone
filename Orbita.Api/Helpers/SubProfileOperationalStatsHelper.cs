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

    public static void MergeAllTimeActivity(
        Dictionary<(Guid AccountId, string SubProfileId), SubProfileOperationalStats> stats,
        IEnumerable<(Guid AccountId, string SubProfileId, DateTime? LastActivity)> rows)
    {
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.SubProfileId))
            {
                continue;
            }

            var key = (row.AccountId, row.SubProfileId.Trim());
            if (stats.TryGetValue(key, out var existing))
            {
                stats[key] = existing with
                {
                    LastActivityUtc = AccountLastActivityHelper.Resolve(
                        existing.LastActivityUtc,
                        row.LastActivity)
                };
            }
            else
            {
                stats[key] = new SubProfileOperationalStats(0, 0, 0, row.LastActivity);
            }
        }
    }

    public static void ApplyEvents(
        Dictionary<(Guid AccountId, string SubProfileId), SubProfileOperationalStats> stats,
        IEnumerable<(Guid AccountId, string? Details, DateTime CreatedAtUtc, string Level)> eventRows,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> subProfileIdsByAccount,
        DateTime todayStartUtc)
    {
        foreach (var (accountId, details, createdAtUtc, level) in eventRows)
        {
            if (!TryResolveSubProfileId(accountId, details, subProfileIdsByAccount, out var subProfileId))
            {
                continue;
            }

            var key = (accountId, subProfileId);
            var countsAsTodayError = createdAtUtc >= todayStartUtc
                && WorkerEventErrorStatsHelper.IsErrorLevel(level);
            if (stats.TryGetValue(key, out var existing))
            {
                stats[key] = existing with
                {
                    TodayEventErrors = existing.TodayEventErrors + (countsAsTodayError ? 1 : 0),
                    LastActivityUtc = AccountLastActivityHelper.Resolve(
                        existing.LastActivityUtc,
                        createdAtUtc)
                };
            }
            else
            {
                stats[key] = new SubProfileOperationalStats(
                    0,
                    0,
                    countsAsTodayError ? 1 : 0,
                    createdAtUtc);
            }
        }
    }

    public static IReadOnlyList<WorkerSubProfileDto>? Enrich(
        IReadOnlyList<WorkerSubProfileDto>? profiles,
        Guid accountId,
        IReadOnlyDictionary<(Guid AccountId, string SubProfileId), SubProfileOperationalStats> stats) =>
        Resolve(profiles, accountId, stats, nameLookup: null);

    public static IReadOnlyList<WorkerSubProfileDto>? Resolve(
        IReadOnlyList<WorkerSubProfileDto>? profiles,
        Guid accountId,
        IReadOnlyDictionary<(Guid AccountId, string SubProfileId), SubProfileOperationalStats> stats,
        IReadOnlyDictionary<(Guid AccountId, string SubProfileId), string>? nameLookup)
    {
        if (profiles is { Count: > 0 })
        {
            return profiles
                .Select(profile => ApplyOperationalStats(profile, accountId, stats))
                .ToList();
        }

        var synthesized = stats
            .Where(x => x.Key.AccountId == accountId)
            .OrderByDescending(x => x.Value.LastActivityUtc ?? DateTime.MinValue)
            .ThenByDescending(x => x.Value.TodayResponses)
            .Select(x => CreateFromOperationalStats(accountId, x.Key.SubProfileId, x.Value, nameLookup))
            .ToList();

        return synthesized.Count == 0 ? profiles : synthesized;
    }

    public static Dictionary<(Guid AccountId, string SubProfileId), string> BuildResponseNameLookup(
        IEnumerable<(Guid AccountId, string SubProfileId, string? SubProfileName)> rows)
    {
        var lookup = new Dictionary<(Guid, string), string>();
        foreach (var (accountId, subProfileId, subProfileName) in rows)
        {
            if (string.IsNullOrWhiteSpace(subProfileId))
            {
                continue;
            }

            var id = subProfileId.Trim();
            var name = string.IsNullOrWhiteSpace(subProfileName) ? id : subProfileName.Trim();
            lookup[(accountId, id)] = name;
        }

        return lookup;
    }

    private static WorkerSubProfileDto ApplyOperationalStats(
        WorkerSubProfileDto profile,
        Guid accountId,
        IReadOnlyDictionary<(Guid AccountId, string SubProfileId), SubProfileOperationalStats> stats)
    {
        stats.TryGetValue((accountId, profile.Id), out var operational);
        operational ??= new SubProfileOperationalStats(0, 0, 0, null);
        var lastActivity = AccountLastActivityHelper.Resolve(
            operational.LastActivityUtc,
            profile.LastIssueAt);
        return profile with
        {
            TodayResponses = operational.TodayResponses,
            TodayDuplicates = operational.TodayDuplicates,
            TodayEventErrors = operational.TodayEventErrors,
            LastActivityUtc = lastActivity
        };
    }

    private static WorkerSubProfileDto CreateFromOperationalStats(
        Guid accountId,
        string subProfileId,
        SubProfileOperationalStats operational,
        IReadOnlyDictionary<(Guid AccountId, string SubProfileId), string>? nameLookup)
    {
        var id = subProfileId.Trim();
        var name = id;
        if (nameLookup is not null
            && nameLookup.TryGetValue((accountId, id), out var resolvedName)
            && !string.IsNullOrWhiteSpace(resolvedName))
        {
            name = resolvedName;
        }

        return new WorkerSubProfileDto(
            id,
            name,
            Category: string.Empty,
            IsCurrent: false,
            Balance: null,
            LastIssueKind: null,
            LastIssueMessage: null,
            LastIssueAt: null,
            TodayResponses: operational.TodayResponses,
            TodayDuplicates: operational.TodayDuplicates,
            TodayEventErrors: operational.TodayEventErrors,
            LastActivityUtc: operational.LastActivityUtc);
    }

    private static bool TryResolveSubProfileId(
        Guid accountId,
        string? details,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> subProfileIdsByAccount,
        out string subProfileId)
    {
        subProfileId = string.Empty;
        if (!subProfileIdsByAccount.TryGetValue(accountId, out var lookup))
        {
            return false;
        }

        var resolvedId = WorkerEventDetailsParser.TryParseDiagnosticSubProfileId(details);
        if (string.IsNullOrWhiteSpace(resolvedId))
        {
            var subProfileName = WorkerEventDetailsParser.TryParseDiagnosticSubProfileName(details);
            if (string.IsNullOrWhiteSpace(subProfileName))
            {
                return false;
            }

            resolvedId = lookup.FirstOrDefault(x =>
                string.Equals(x.Value, subProfileName, StringComparison.OrdinalIgnoreCase)).Key;
        }

        if (string.IsNullOrWhiteSpace(resolvedId))
        {
            return false;
        }

        subProfileId = resolvedId.Trim();
        return true;
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
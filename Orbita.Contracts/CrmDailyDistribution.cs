using System.Security.Cryptography;
using System.Text;

namespace Orbita.Contracts;

/// <summary>
/// Pure rules for the daily distribution of native Orbita CRM cards.
/// Leads and NDZ are deliberately independent pools.
/// </summary>
public static class CrmDailyDistribution
{
    public const string LeadPool = "lead";
    public const string NdzPool = "ndz";
    public static readonly TimeSpan ShiftCollectionDelay = TimeSpan.FromMinutes(5);

    public sealed record Counter(string ManagerUserId, int AssignedCount);
    public sealed record Assignment(Guid CardId, string ManagerUserId);

    public static DateOnly BusinessDate(DateTime utcNow) => CrmShiftRules.BusinessDate(utcNow);

    public static bool IsNdz(string? stage) =>
        string.Equals(stage, CrmStages.Ndz73, StringComparison.Ordinal)
        || string.Equals(stage, CrmStages.Ndz26, StringComparison.Ordinal);

    /// <summary>
    /// Deterministically shuffles cards and managers and produces quotas whose
    /// difference is at most one. A retry for the same office/day/pool yields
    /// the same plan.
    /// </summary>
    public static IReadOnlyList<Assignment> BuildBalancedPlan(
        IEnumerable<Guid> cardIds,
        IEnumerable<string> managerUserIds,
        Guid officeId,
        DateOnly localDate,
        string pool)
    {
        var managers = managerUserIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => StableKey($"{officeId:N}|{localDate:yyyyMMdd}|{pool}|manager|{x}"), StringComparer.Ordinal)
            .ToList();
        if (managers.Count == 0)
        {
            return [];
        }

        var cards = cardIds
            .Distinct()
            .OrderBy(x => StableKey($"{officeId:N}|{localDate:yyyyMMdd}|{pool}|card|{x:N}"), StringComparer.Ordinal)
            .ToList();
        var result = new List<Assignment>(cards.Count);
        for (var index = 0; index < cards.Count; index++)
        {
            result.Add(new Assignment(cards[index], managers[index % managers.Count]));
        }

        return result;
    }

    /// <summary>
    /// Selects the manager who has received the fewest leads today. Ties are
    /// resolved by a persisted round-robin cursor, never by current workload.
    /// </summary>
    public static string? SelectNextForNewLead(
        IEnumerable<string> eligibleManagerUserIds,
        IEnumerable<Counter> counters,
        string? lastManagerUserId)
    {
        var eligible = eligibleManagerUserIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        if (eligible.Count == 0)
        {
            return null;
        }

        var counts = counters
            .GroupBy(x => x.ManagerUserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.AssignedCount), StringComparer.Ordinal);
        var minimum = eligible.Min(x => counts.GetValueOrDefault(x));
        var tied = eligible.Where(x => counts.GetValueOrDefault(x) == minimum).ToList();
        if (tied.Count == 1)
        {
            return tied[0];
        }

        var lastIndex = string.IsNullOrWhiteSpace(lastManagerUserId)
            ? -1
            : eligible.IndexOf(lastManagerUserId);
        for (var offset = 1; offset <= eligible.Count; offset++)
        {
            var candidate = eligible[(lastIndex + offset + eligible.Count) % eligible.Count];
            if (tied.Contains(candidate, StringComparer.Ordinal))
            {
                return candidate;
            }
        }

        return tied[0];
    }

    private static string StableKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

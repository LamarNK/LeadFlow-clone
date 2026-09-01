using System.Security.Cryptography;
using System.Text;

namespace Orbita.Contracts;

/// <summary>
/// Pure rules for the daily distribution of native Orbita CRM cards.
/// Leads, NDZ and office-specific service stages are deliberately independent pools.
/// </summary>
public static class CrmDailyDistribution
{
    public const int PoolKeyMaxLength = 16;
    public const string LeadPool = "lead";
    public const string NdzPool = "ndz";
    public const string UnavailableSubstitutePool = "unavailable";
    public const string ThirdOfficeName = "3 офис";
    public const string UnavailableSubstituteStage = "Недоступные подменные";
    public static readonly TimeSpan ShiftCollectionDelay = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyList<string> PrimaryNdzAliases =
    [
        "НДЗ",
        CrmStages.Ndz73
    ];

    private static readonly IReadOnlyList<string> SecondaryNdzAliases =
    [
        "НДЗ 2",
        CrmStages.Ndz26
    ];

    public sealed record Counter(string ManagerUserId, int AssignedCount);
    public sealed record Assignment(Guid CardId, string ManagerUserId);
    public sealed record NdzStageSet(string? PrimaryStage, IReadOnlyList<string> Stages);

    public static DateOnly BusinessDate(DateTime utcNow) => CrmShiftRules.BusinessDate(utcNow);

    public static bool IsNdz(string? stage) =>
        IsPrimaryNdz(stage) || IsSecondaryNdz(stage);

    public static bool IsPrimaryNdz(string? stage) =>
        MatchesAlias(stage, PrimaryNdzAliases);

    public static bool IsSecondaryNdz(string? stage) =>
        MatchesAlias(stage, SecondaryNdzAliases);

    /// <summary>
    /// Resolves the actual NDZ stage names configured for an office. Production
    /// offices use "НДЗ"/"НДЗ 2", while older/local funnels can still use
    /// "НДЗ 73"/"НДЗ 2.6". Returned values preserve the office configuration
    /// exactly so distribution never creates a foreign stage name.
    /// </summary>
    public static NdzStageSet ResolveNdzStages(IEnumerable<string>? officeStages)
    {
        var stages = (officeStages ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var primary = stages.FirstOrDefault(IsPrimaryNdz);
        var recognized = stages
            .Where(IsNdz)
            .ToList();
        return new NdzStageSet(primary, recognized);
    }

    /// <summary>
    /// Resolves the third office's unavailable-substitute stage while preserving
    /// the exact configured label. Other offices must never distribute this pool.
    /// </summary>
    public static string? ResolveUnavailableSubstituteStage(
        string? officeName,
        IEnumerable<string>? officeStages)
    {
        if (!string.Equals(
                officeName?.Trim(),
                ThirdOfficeName,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return (officeStages ?? [])
            .FirstOrDefault(stage => string.Equals(
                stage?.Trim(),
                UnavailableSubstituteStage,
                StringComparison.OrdinalIgnoreCase));
    }

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
    /// Selects the next active manager using only the persisted round-robin
    /// cursor. Earlier assignments are deliberately ignored: when the active
    /// roster changes, each newly arriving batch is shared evenly among the
    /// managers who are on shift at that moment.
    /// </summary>
    public static string? SelectNextForNewLead(
        IEnumerable<string> eligibleManagerUserIds,
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

        var lastIndex = string.IsNullOrWhiteSpace(lastManagerUserId)
            ? -1
            : eligible.IndexOf(lastManagerUserId);
        return eligible[(lastIndex + 1 + eligible.Count) % eligible.Count];
    }

    private static string StableKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool MatchesAlias(string? stage, IReadOnlyList<string> aliases)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return false;
        }

        var normalized = stage.Trim();
        return aliases.Any(alias => string.Equals(normalized, alias, StringComparison.OrdinalIgnoreCase));
    }
}

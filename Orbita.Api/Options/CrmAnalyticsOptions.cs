namespace Orbita.Api.Options;

public sealed class CrmAnalyticsOptions
{
    public const string SectionName = "CrmAnalytics";

    /// <summary>
    /// Additional stages that are independent card entry points rather than
    /// sequential conversion steps. Keys are office IDs, values are exact stage names.
    /// </summary>
    public Dictionary<string, string[]> AlternateEntryStagesByOffice { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional office-local aliases to a standard milestone; never changes board stages.</summary>
    public Dictionary<string, Dictionary<string, string>> StageMilestonesByOffice { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string ResolveMilestone(Guid officeId, string stage)
    {
        if (StageMilestonesByOffice.TryGetValue(officeId.ToString("D"), out var aliases))
        {
            var alias = aliases.FirstOrDefault(x => string.Equals(x.Key.Trim(), stage.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(alias.Value)) return alias.Value.Trim();
        }
        return stage.Trim();
    }

    public IReadOnlySet<string> GetAlternateEntryStages(Guid officeId)
    {
        if (!AlternateEntryStagesByOffice.TryGetValue(officeId.ToString("D"), out var stages)
            || stages is null
            || stages.Length == 0)
        {
            return EmptyStages;
        }

        return stages
            .Where(stage => !string.IsNullOrWhiteSpace(stage))
            .Select(stage => stage.Trim())
            .ToHashSet(StringComparer.Ordinal);
    }

    private static readonly IReadOnlySet<string> EmptyStages =
        new HashSet<string>(StringComparer.Ordinal);
}

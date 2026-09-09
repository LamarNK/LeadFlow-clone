using System.Text.RegularExpressions;
using Orbita.Api.Options;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>
/// Sales-approved rules, independent of the current owner, card creation date and shifts.
/// Aliases only classify analytics: they never rename or reorder an office's board.
/// </summary>
internal sealed partial class CrmSalesRules(CrmAnalyticsOptions options)
{
    internal const string ConfigurationSuffix = " (воронка обновлена)";

    public string NormalizeStage(Guid officeId, string stage) =>
        Normalize(options.ResolveMilestone(officeId, stage));

    public bool IsContactSource(Guid officeId, string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return false;
        var value = NormalizeStage(officeId, stage);
        return value is "лид" or "лид (важный)" or "лид(важный)" or "подменка"
            or "недоступные подменные"
            || NdzPrefix().IsMatch(value)
            || options.GetAlternateEntryStages(officeId).Any(x => Normalize(x) == Normalize(stage));
    }

    public bool IsContactDestination(Guid officeId, string stage)
    {
        var value = NormalizeStage(officeId, stage);
        return value is "переговоры" or "переговоры долгосрок" or "анкета";
    }

    public bool IsMilestone(Guid officeId, string stage, string milestone) =>
        NormalizeStage(officeId, stage) == Normalize(milestone);

    public static bool IsSuccess(string? details) => Normalize(Reason(details)) == Normalize(CrmCloseReasons.Success);
    public static string Reason(string? details) => CrmActivityDetails.Split(details).Details?.Trim() ?? "";
    public static bool IsNoAnswer(string? details) => NdzPrefix().IsMatch(Normalize(Reason(details)));

    public static (string From, string To)? Transition(string? details)
    {
        var text = CrmActivityDetails.Split(details).Details?.Trim();
        if (string.IsNullOrEmpty(text) || text.EndsWith(ConfigurationSuffix, StringComparison.Ordinal)) return null;
        var index = text.IndexOf('→');
        var length = 1;
        if (index < 0) { index = text.IndexOf("->", StringComparison.Ordinal); length = 2; }
        if (index < 0) return null;
        var from = text[..index].Trim();
        var to = text[(index + length)..].Trim();
        return from.Length == 0 || to.Length == 0 || Normalize(from) == Normalize(to) ? null : (from, to);
    }

    public static string Normalize(string value) => Spaces().Replace(value.Trim().ToLowerInvariant().Replace('ё', 'е'), " ");

    [GeneratedRegex(@"^ндз(?:\s|\d|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NdzPrefix();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}

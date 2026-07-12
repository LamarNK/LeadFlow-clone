using System.Text;
using System.Text.RegularExpressions;

namespace Orbita.Contracts;

public static partial class CityNormalizer
{
    private static readonly (string Pattern, string Replacement)[] AbbreviationRules =
    [
        (@"^рп\.?\s+", "рабочий поселок "),
        (@"^пгт\.?\s+", "поселок городского типа "),
        (@"^пос\.?\s+", "поселок "),
        (@"^г\.?\s+", "город "),
        (@"^с\.?\s+", "село "),
        (@"^д\.?\s+", "деревня "),
        (@"^ст\.?\s+", "станица "),
        (@"^х\.?\s+", "хутор "),
    ];

    public static string Normalize(string? city)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            return string.Empty;
        }

        var segments = city
            .Trim()
            .ToLowerInvariant()
            .Split(',', StringSplitOptions.TrimEntries)
            .Select(NormalizeSegment)
            .Where(static x => x.Length > 0);

        return string.Join(", ", segments);
    }

    public static bool IsMatch(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        return normalizedLeft.Length > 0
            && normalizedRight.Length > 0
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private static string NormalizeSegment(string segment)
    {
        var normalized = WhitespaceRegex().Replace(segment, " ").Trim();
        foreach (var (pattern, replacement) in AbbreviationRules)
        {
            normalized = Regex.Replace(normalized, pattern, replacement, RegexOptions.CultureInvariant);
        }

        return WhitespaceRegex().Replace(normalized, " ").Trim();
    }
}

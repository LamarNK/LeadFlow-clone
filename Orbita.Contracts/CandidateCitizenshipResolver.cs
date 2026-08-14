using System.Text.RegularExpressions;

namespace Orbita.Contracts;

/// <summary>Extracts an explicitly stated citizenship from Avito card or chat text.</summary>
public static partial class CandidateCitizenshipResolver
{
    public const int MaxLength = 128;

    public static string Resolve(string? explicitValue, params string?[] texts)
    {
        var normalizedExplicit = Normalize(explicitValue);
        if (normalizedExplicit.Length > 0)
        {
            return normalizedExplicit;
        }

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var match = CitizenshipPattern().Match(text);
            if (match.Success)
            {
                var value = Normalize(match.Groups[1].Value);
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(value, @"\s+", " ")
            .Trim(' ', ':', '-', '–', '—', '·', '•', '|', ',', ';', '.', '"');
        return normalized.Length <= MaxLength ? normalized : normalized[..MaxLength].TrimEnd();
    }

    [GeneratedRegex(
        @"\bгражданство\s*(?:[:\-–—]\s*)?(.+?)(?=\s+(?:возраст|фио|опыт|пол|город|телефон)\s*(?:[:\-–—])|[·•|,;\r\n]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CitizenshipPattern();
}

using System.Text.RegularExpressions;

namespace Orbita.Contracts;

public static class CandidateGenders
{
    public const string Male = "male";
    public const string Female = "female";
    public const string Unknown = "unknown";

    /// <summary>
    /// Avito demographic line: «Мужчина · 54 года», optional trailing fields.
    /// Requires gender word next to a separator/age so vacancy prose is less likely to match.
    /// </summary>
    private static readonly Regex MaleCardLineRegex = new(
        @"(?<![а-яё])мужчина(?![а-яё])(?:\s*[·•|,\-—]\s*|\s+(?=\d{1,2}\s*(?:лет|года|год)))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FemaleCardLineRegex = new(
        @"(?<![а-яё])женщина(?![а-яё])(?:\s*[·•|,\-—]\s*|\s+(?=\d{1,2}\s*(?:лет|года|год)))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Bare label on its own short line (legacy cards).</summary>
    private static readonly Regex MaleBareRegex = new(
        @"^(?<![а-яё])мужчина(?![а-яё])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FemaleBareRegex = new(
        @"^(?<![а-яё])женщина(?![а-яё])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? ParseFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        if (MaleCardLineRegex.IsMatch(normalized) || MaleBareRegex.IsMatch(normalized))
        {
            return Male;
        }

        if (FemaleCardLineRegex.IsMatch(normalized) || FemaleBareRegex.IsMatch(normalized))
        {
            return Female;
        }

        return null;
    }

    public static string NormalizeFilterValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            Male => Male,
            Female => Female,
            Unknown => Unknown,
            _ => string.Empty
        };
    }

    public static string FormatLabel(string? gender) => gender switch
    {
        Male => "Мужчина",
        Female => "Женщина",
        _ => "—"
    };

    public static string FormatFilterLabel(string? gender) => gender switch
    {
        Male => "Мужчина",
        Female => "Женщина",
        Unknown => "Не указан",
        _ => string.Empty
    };
}
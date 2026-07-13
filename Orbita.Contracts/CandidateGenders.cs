using System.Text.RegularExpressions;

namespace Orbita.Contracts;

public static class CandidateGenders
{
    public const string Male = "male";
    public const string Female = "female";
    public const string Unknown = "unknown";

    private static readonly Regex MaleRegex = new(
        @"(?<![а-яё])мужчина(?![а-яё])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FemaleRegex = new(
        @"(?<![а-яё])женщина(?![а-яё])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? ParseFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        if (MaleRegex.IsMatch(normalized))
        {
            return Male;
        }

        if (FemaleRegex.IsMatch(normalized))
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
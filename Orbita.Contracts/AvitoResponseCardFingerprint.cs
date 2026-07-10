using System.Text.RegularExpressions;

namespace Orbita.Contracts;

/// <summary>
/// Стабильный ключ отклика Avito по полям карточки без телефона (для пропуска popup до раскрытия номера).
/// </summary>
public static class AvitoResponseCardFingerprint
{
    private static readonly Regex VacancyIdRegex = new(
        @"(?:/|_)(\d{5,})(?:\?|$|/|)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MessengerChannelRegex = new(
        @"/profile/messenger/channel/([^/?#]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AgeTextRegex = new(
        @"^(\d{1,2})\s*(?:лет|года|год)?\.?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Единый формат возраста для fingerprint (как в JS: «42 лет»).</summary>
    public static string NormalizeAgeText(string? ageText, int? age = null)
    {
        if (!string.IsNullOrWhiteSpace(ageText))
        {
            var trimmed = ageText.Trim();
            var match = AgeTextRegex.Match(trimmed);
            if (match.Success)
            {
                return $"{match.Groups[1].Value} лет";
            }
        }

        if (age is >= 0 and <= 120)
        {
            return $"{age.Value} лет";
        }

        return string.Empty;
    }

    public static string Build(
        string fullName,
        string vacancy,
        string city,
        string? vacancyUrl,
        string? messengerUrl,
        string? ageText = null)
    {
        var messengerKey = ExtractMessengerChannelKey(messengerUrl);
        if (!string.IsNullOrEmpty(messengerKey))
        {
            return $"avito-card:msg:{messengerKey}";
        }

        var vacancyId = ExtractVacancyId(vacancyUrl);
        var payload = string.Join(
            "\u001f",
            new[]
            {
                Normalize(fullName),
                vacancyId ?? Normalize(vacancy),
                Normalize(city),
                Normalize(NormalizeAgeText(ageText))
            });

        return $"avito-card:{Fnv1a32Hex(payload)}";
    }

    public static string? ExtractVacancyId(string? vacancyUrl)
    {
        if (string.IsNullOrWhiteSpace(vacancyUrl))
        {
            return null;
        }

        var match = VacancyIdRegex.Match(vacancyUrl.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string? ExtractMessengerChannelKey(string? messengerUrl)
    {
        if (string.IsNullOrWhiteSpace(messengerUrl))
        {
            return null;
        }

        var match = MessengerChannelRegex.Match(messengerUrl.Trim());
        if (!match.Success)
        {
            return null;
        }

        var key = match.Groups[1].Value.Trim();
        return string.IsNullOrWhiteSpace(key) ? null : key.ToLowerInvariant();
    }

    internal static string Normalize(string value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");

    internal static string Fnv1a32Hex(string text)
    {
        uint hash = 2166136261;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 16777619;
        }

        return hash.ToString("x");
    }
}
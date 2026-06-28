using System.Globalization;
using System.Text.RegularExpressions;

namespace Orbita.Web.Formatting;

public static partial class ResponseDisplay
{
    public static string DisplayAuthor(string? fullName) =>
        string.IsNullOrWhiteSpace(fullName) ? "Неизвестный пользователь" : fullName.Trim();

    public static bool IsPhoneHidden(string? phoneRaw, string? phoneNormalized) =>
        string.IsNullOrWhiteSpace(phoneRaw) && string.IsNullOrWhiteSpace(phoneNormalized);

    public static string FormatPhone(string? phoneRaw, string? phoneNormalized)
    {
        var digits = new string((phoneNormalized ?? phoneRaw ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith('7'))
        {
            return $"+7 {digits[1..4]} {digits[4..7]}-{digits[7..9]}-{digits[9..11]}";
        }

        if (digits.Length == 10)
        {
            return $"+7 {digits[..3]} {digits[3..6]}-{digits[6..8]}-{digits[8..10]}";
        }

        return string.IsNullOrWhiteSpace(phoneRaw) ? string.Empty : phoneRaw.Trim();
    }

    public static string DisplayAdId(string sourceResponseId, string vacancyUrl)
    {
        if (!string.IsNullOrWhiteSpace(sourceResponseId))
        {
            return sourceResponseId.Trim();
        }

        var match = AvitoItemIdRegex().Match(vacancyUrl ?? string.Empty);
        return match.Success ? match.Groups[1].Value : "—";
    }

    public static string FormatCreatedAtLocal(DateTime createdAtUtc) =>
        createdAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    public static string FormatAverageResponseMinutes(double? minutes) =>
        minutes is > 0 and var avg ? $"{(int)Math.Round(avg)} минут" : "—";

    [GeneratedRegex(@"/(\d{6,})(?:\?|$)", RegexOptions.CultureInvariant)]
    private static partial Regex AvitoItemIdRegex();
}
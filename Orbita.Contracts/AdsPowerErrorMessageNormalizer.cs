using System.Text.RegularExpressions;

namespace Orbita.Contracts;

/// <summary>
/// Человекочитаемые формулировки типовых ошибок Local API AdsPower для панели Орбита.
/// </summary>
public static partial class AdsPowerErrorMessageNormalizer
{
    public static bool LooksLikeProfileInUse(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("is being used by", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not allowed to open", StringComparison.OrdinalIgnoreCase)
            || message.Contains("уже открыт пользователем", StringComparison.OrdinalIgnoreCase)
            || message.Contains("уже используется пользователем", StringComparison.OrdinalIgnoreCase));

    public static string NormalizeForDisplay(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return message ?? string.Empty;
        }

        return TryFormatProfileInUse(message, out var formatted)
            ? formatted
            : message;
    }

    public static bool TryFormatProfileInUse(string message, out string formatted)
    {
        var match = ProfileInUseRegex().Match(message);
        if (!match.Success)
        {
            formatted = string.Empty;
            return false;
        }

        var profile = match.Groups["profile"].Value;
        var user = match.Groups["user"].Value;
        formatted =
            $"Профиль AdsPower «{profile}» уже открыт пользователем {user}. " +
            "Закройте браузер в AdsPower или дождитесь освобождения.";
        return true;
    }

    [GeneratedRegex(
        @"\[(?<profile>[^\]]+)\]\s+is being used by\s+\[(?<user>[^\]]+)\](?:\s+and is not allowed to open)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfileInUseRegex();
}
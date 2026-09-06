using System.Text.RegularExpressions;

namespace Orbita.Contracts;

/// <summary>
/// Человекочитаемые формулировки типовых ошибок браузерных провайдеров для панели Орбита.
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

        var displayMessage = TryFormatProfileInUse(message, out var formatted)
            ? formatted
            : message;

        return NormalizeLegacyProviderReferences(displayMessage);
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
            $"Браузерный профиль «{profile}» уже открыт пользователем {user}. " +
            "Закройте браузерный профиль или дождитесь освобождения.";
        return true;
    }

    /// <summary>
    /// Старые события содержат название первого поддержанного провайдера. В журнале
    /// оно вводит в заблуждение, поскольку профиль может быть запущен другим способом.
    /// Технические настройки и URL Local API этим методом не меняются.
    /// </summary>
    private static string NormalizeLegacyProviderReferences(string message)
    {
        var normalized = LegacyProviderReferenceRegex().Replace(message, match => match.Value switch
        {
            var value when value.StartsWith("лимит частоты", StringComparison.OrdinalIgnoreCase)
                => "лимит частоты браузерного провайдера",
            var value when value.StartsWith("дневной лимит", StringComparison.OrdinalIgnoreCase)
                => "дневной лимит браузерного провайдера",
            var value when value.StartsWith("лимит", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("rate limit", StringComparison.OrdinalIgnoreCase)
                => "лимит браузерного провайдера",
            var value when value.StartsWith("профиль", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("браузер", StringComparison.OrdinalIgnoreCase)
                => "браузерный профиль",
            var value when value.StartsWith("в ", StringComparison.OrdinalIgnoreCase)
                => "в браузерном приложении",
            _ => "браузерный провайдер"
        });

        return normalized;
    }

    [GeneratedRegex(
        @"\[(?<profile>[^\]]+)\]\s+is being used by\s+\[(?<user>[^\]]+)\](?:\s+and is not allowed to open)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfileInUseRegex();

    [GeneratedRegex(
        @"\bлимит\s+частоты\s+AdsPower\b|\bдневной\s+лимит\s+AdsPower\b|\b(?:лимит|rate\s+limit)\s+AdsPower\b|\b(?:профиль|браузер)\s+AdsPower\b|\bв\s+AdsPower\b|(?<![./])\bAdsPower\b(?![./])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyProviderReferenceRegex();
}

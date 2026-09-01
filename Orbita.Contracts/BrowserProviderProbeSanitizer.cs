using System.Text.RegularExpressions;

namespace Orbita.Contracts;

public static class BrowserProviderProbeSanitizer
{
    public const int MaxMessageLength = 180;

    private static readonly Regex BearerRegex = new(
        @"(?i)bearer\s+\S+",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex CredentialUrlRegex = new(
        @"(?i)(?:(?:https?|wss?)://)?[^/\s:@]+:[^/\s:@]+@",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    public static string Sanitize(string? message, params string?[] secrets) =>
        Sanitize(message, MaxMessageLength, "Не удалось проверить подключение.", secrets);

    public static string Sanitize(
        string? message,
        int maxLength,
        string fallback,
        params string?[] secrets)
    {
        var result = StripSecrets(message, secrets);
        if (result.Length == 0)
        {
            result = string.IsNullOrWhiteSpace(fallback)
                ? "Не удалось проверить подключение."
                : fallback.Trim();
        }

        if (maxLength <= 0 || result.Length <= maxLength)
        {
            return result;
        }

        return result[..maxLength].TrimEnd() + "…";
    }

    public static string StripSecrets(string? message, params string?[] secrets)
    {
        var result = string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : message.Trim();
        if (result.Length == 0)
        {
            return result;
        }

        result = CredentialUrlRegex.Replace(result, string.Empty);
        result = BearerRegex.Replace(result, string.Empty);

        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret))
            {
                continue;
            }

            result = result.Replace(secret, string.Empty, StringComparison.Ordinal);
        }

        return result.Replace("  ", " ", StringComparison.Ordinal).Trim();
    }

    public static string FromException(Exception exception, params string?[] secrets)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var raw = exception is HttpRequestException http
            ? http.HttpRequestError == HttpRequestError.ConnectionError
                ? "Сервис недоступен."
                : "Ошибка сети."
            : exception.Message;
        return Sanitize(raw, secrets);
    }
}

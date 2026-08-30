using System.Net;
using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.Worker;

public static class BrowserProviderProbeSanitizer
{
    public const int MaxMessageLength = 180;

    private static readonly Regex BearerRegex = new(
        @"(?i)bearer\s+\S+",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex CredentialUrlRegex = new(
        @"(?i)(?:https?|wss?)://[^/\s:@]+:[^/\s:@]+@",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    public static string Sanitize(string? message, params string?[] secrets)
    {
        var result = string.IsNullOrWhiteSpace(message)
            ? "Не удалось проверить подключение."
            : message.Trim();

        result = CredentialUrlRegex.Replace(result, string.Empty);
        result = BearerRegex.Replace(result, string.Empty);

        foreach (var secret in secrets)
        {
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 4)
            {
                continue;
            }

            result = result.Replace(secret, string.Empty, StringComparison.Ordinal);
        }

        result = result.Replace("  ", " ", StringComparison.Ordinal).Trim();
        if (result.Length == 0)
        {
            result = "Не удалось проверить подключение.";
        }

        return result.Length <= MaxMessageLength
            ? result
            : result[..MaxMessageLength].TrimEnd() + "…";
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

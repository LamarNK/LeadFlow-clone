using System.Globalization;
using System.Text.RegularExpressions;

namespace Orbita.Contracts;

/// <summary>
/// HTTP/HTTPS-прокси обычного Chrome. SOCKS не поддерживается: авторизация SOCKS в Chromium отдельная задача.
/// Адрес только <c>host:port</c> — без схемы и без <c>user:password@</c>, чтобы секрет не попал в args и логи.
/// </summary>
public static class LocalChromeProxyRules
{
    public const int MaxAddressLength = 255;
    public const int MaxUsernameLength = 255;
    public const int MaxPasswordLength = 256;
    public const string HttpType = "http";

    public const string StatusNotConfigured = "не настроен";
    public const string StatusConfigured = "настроен";
    public const string StatusCheckFailed = "ошибка проверки";

    public const string BrowserFree = "Свободен";
    public const string BrowserOpenedManually = "Открыт вручную";
    public const string BrowserMonitoring = "Мониторится";

    public const string AvitoStartUrl = "https://www.avito.ru/";

    private static readonly Regex HostPortRegex = new(
        @"^(?<host>[A-Za-z0-9](?:[A-Za-z0-9.-]{0,253}[A-Za-z0-9])?|[A-Za-z0-9]|\[(?:[0-9A-Fa-f:]+)\]):(?<port>\d{1,5})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    public static bool TryNormalizeAddress(string? value, out string? normalized, out string? error)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Укажите адрес прокси в формате host:port.";
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxAddressLength)
        {
            error = $"Адрес прокси не должен превышать {MaxAddressLength} символов.";
            return false;
        }

        if (trimmed.Contains(' ', StringComparison.Ordinal)
            || trimmed.Contains('\t', StringComparison.Ordinal))
        {
            error = "Адрес прокси не должен содержать пробелы.";
            return false;
        }

        if (trimmed.Contains('@', StringComparison.Ordinal))
        {
            error = "Не указывайте логин и пароль в адресе прокси. Используйте отдельные поля.";
            return false;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            error = "Укажите адрес без схемы, в формате host:port.";
            return false;
        }

        if (trimmed.Contains('/', StringComparison.Ordinal)
            || trimmed.Contains('?', StringComparison.Ordinal)
            || trimmed.Contains('#', StringComparison.Ordinal)
            || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            error = "Укажите адрес прокси в формате host:port.";
            return false;
        }

        Match match;
        try
        {
            match = HostPortRegex.Match(trimmed);
        }
        catch (RegexMatchTimeoutException)
        {
            error = "Укажите адрес прокси в формате host:port.";
            return false;
        }

        if (!match.Success)
        {
            error = "Укажите адрес прокси в формате host:port.";
            return false;
        }

        if (!int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            error = "Порт прокси должен быть числом от 1 до 65535.";
            return false;
        }

        var host = match.Groups["host"].Value;
        if (host.Contains("..", StringComparison.Ordinal))
        {
            error = "Укажите адрес прокси в формате host:port.";
            return false;
        }

        normalized = $"{host}:{port}";
        error = null;
        return true;
    }

    public static bool TryNormalizeUsername(string? value, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxUsernameLength)
        {
            error = $"Логин прокси не должен превышать {MaxUsernameLength} символов.";
            return false;
        }

        if (trimmed.Contains('@', StringComparison.Ordinal) && trimmed.Contains(':', StringComparison.Ordinal))
        {
            error = "Не указывайте логин прокси в формате user:password@host.";
            return false;
        }

        normalized = trimmed;
        return true;
    }

    public static string Status(bool enabled, string? address)
    {
        if (!enabled || string.IsNullOrWhiteSpace(address))
        {
            return StatusNotConfigured;
        }

        return TryNormalizeAddress(address, out _, out _)
            ? StatusConfigured
            : StatusCheckFailed;
    }

    public static string BrowserSessionStatus(bool isMonitoring, bool isOpenedManually)
    {
        if (isMonitoring)
        {
            return BrowserMonitoring;
        }

        return isOpenedManually ? BrowserOpenedManually : BrowserFree;
    }

    public static bool CanOpenBrowser(bool isLocal, bool providerEnabled, bool isMonitoring) =>
        isLocal && providerEnabled && !isMonitoring;

    /// <summary>
    /// Chromium launch arg. Никогда не включает логин и пароль — только <c>http://host:port</c>.
    /// </summary>
    public static string[]? ToChromiumArgs(bool enabled, string? address)
    {
        if (!enabled || !TryNormalizeAddress(address, out var normalized, out _))
        {
            return null;
        }

        return [$"--proxy-server=http://{normalized}"];
    }

    public static string SanitizeError(string? message, params string?[] secrets) =>
        BrowserProviderProbeSanitizer.Sanitize(
            string.IsNullOrWhiteSpace(message) ? "Не удалось применить прокси обычного браузера." : message,
            secrets);
}

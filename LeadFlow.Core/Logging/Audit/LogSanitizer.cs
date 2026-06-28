using System.Text.RegularExpressions;

namespace LeadFlow.Core.Logging.Audit;

/// <summary>
/// Маскирование ключей и типичных паттернов (токены, пароли) перед записью в лог.
/// </summary>
public static class LogSanitizer
{
    private static readonly HashSet<string> SensitiveKeyParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "secret", "token", "apikey", "api_key", "authorization",
        "cookie", "set-cookie", "credit", "ssn", "pin"
    };

    private static readonly string[] JsonSensitiveKeyNames =
    {
        "password", "token", "secret", "apiKey", "authorization", "api_key", "access_token"
    };

    /// <summary>Маскирует значения по подозрительным именам ключей (плоский словарь).</summary>
    public static Dictionary<string, object?> SanitizeDictionary(IReadOnlyDictionary<string, object?>? source)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source == null) return d;
        foreach (var kv in source)
        {
            var key = kv.Key ?? "";
            if (ShouldMaskKey(key))
            {
                d[key] = "***";
                continue;
            }

            switch (kv.Value)
            {
                case string s:
                    d[key] = RedactSensitivePatterns(s);
                    break;
                case IReadOnlyDictionary<string, object?> nested:
                    d[key] = SanitizeDictionary(nested);
                    break;
                default:
                    d[key] = kv.Value;
                    break;
            }
        }

        return d;
    }

    public static bool ShouldMaskKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        foreach (var part in SensitiveKeyParts)
        {
            if (key.Contains(part, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Обрезка и маскирование типичных паттернов в произвольной строке (тело запроса и т.д.).</summary>
    public static string RedactSensitivePatterns(string? text, int maxLen = 16384)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = text.Length <= maxLen ? text : text.Substring(0, maxLen) + "…";
        try
        {
            foreach (var p in JsonSensitiveKeyNames)
            {
                s = Regex.Replace(s, $@"""{p}""\s*:\s*""[^""]*""",
                    $"\"{p}\":\"***\"", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // ignore
        }

        return s;
    }
}

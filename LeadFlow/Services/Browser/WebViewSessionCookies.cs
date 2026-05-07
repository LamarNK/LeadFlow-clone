using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace LeadFlow.Services.Browser;

/// <summary>
/// Подставляет куки из JSON (формат экспорта <see cref="ProfileCookiesService"/>) в сессию WebView2 до первой навигации.
/// </summary>
internal static class WebViewSessionCookies
{
    public sealed record ApplyCookiesReport(
        int TotalEntries,
        int Applied,
        int SkippedInvalid,
        int SkippedErrors,
        string? Summary);

    public static Task<ApplyCookiesReport> ApplyFromStoredCookiesJsonAsync(CoreWebView2 core, string? cookiesJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookiesJson))
        {
            return Task.FromResult(new ApplyCookiesReport(0, 0, 0, 0, "Cookies JSON пустой - импорт пропущен."));
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(cookiesJson);
        }
        catch (JsonException)
        {
            return Task.FromResult(new ApplyCookiesReport(0, 0, 0, 0, "Cookies JSON невалидный - импорт пропущен."));
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Task.FromResult(new ApplyCookiesReport(0, 0, 0, 0, "Ожидался JSON-массив cookies - импорт пропущен."));
            }

            var manager = core.CookieManager;
            var total = 0;
            var applied = 0;
            var skippedInvalid = 0;
            var skippedErrors = 0;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                total++;
                cancellationToken.ThrowIfCancellationRequested();
                if (el.ValueKind != JsonValueKind.Object)
                {
                    skippedInvalid++;
                    continue;
                }

                if (!el.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                {
                    skippedInvalid++;
                    continue;
                }

                var name = nameProp.GetString();
                if (string.IsNullOrEmpty(name))
                {
                    skippedInvalid++;
                    continue;
                }

                var value = ReadJsonValueAsString(el, "value");
                if (!el.TryGetProperty("domain", out var domainProp) || domainProp.ValueKind != JsonValueKind.String)
                {
                    skippedInvalid++;
                    continue;
                }

                var domain = domainProp.GetString()?.Trim();
                if (string.IsNullOrEmpty(domain))
                {
                    skippedInvalid++;
                    continue;
                }

                var path = "/";
                if (el.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String)
                {
                    var p = pathProp.GetString();
                    if (!string.IsNullOrEmpty(p))
                    {
                        path = p;
                    }
                }

                CoreWebView2Cookie cookie;
                try
                {
                    cookie = manager.CreateCookie(name, value, domain, path);
                }
                catch (ArgumentException)
                {
                    skippedErrors++;
                    continue;
                }

                if (el.TryGetProperty("secure", out var sec))
                {
                    if (sec.ValueKind == JsonValueKind.True)
                    {
                        cookie.IsSecure = true;
                    }
                    else if (sec.ValueKind == JsonValueKind.False)
                    {
                        cookie.IsSecure = false;
                    }
                }

                if (el.TryGetProperty("httpOnly", out var httpOnlyProp))
                {
                    if (httpOnlyProp.ValueKind == JsonValueKind.True)
                    {
                        cookie.IsHttpOnly = true;
                    }
                    else if (httpOnlyProp.ValueKind == JsonValueKind.False)
                    {
                        cookie.IsHttpOnly = false;
                    }
                }

                if (TryParseCookieExpires(el, out var expiresUtc))
                {
                    cookie.Expires = expiresUtc;
                }

                cookie.SameSite = ParseSameSite(el);

                // SameSite=None в Chromium обычно требует Secure
                if (cookie.SameSite == CoreWebView2CookieSameSiteKind.None)
                {
                    cookie.IsSecure = true;
                }

                try
                {
                    manager.AddOrUpdateCookie(cookie);
                    applied++;
                }
                catch (ArgumentException)
                {
                    // неверная комбинация domain/path/samesite для движка
                    skippedErrors++;
                }
                catch (InvalidOperationException)
                {
                    // сессия / cookie manager недоступен
                    skippedErrors++;
                }
            }

            var summary = $"Импорт cookies: всего={total}, применено={applied}, пропущено невалидных={skippedInvalid}, ошибок={skippedErrors}.";
            return Task.FromResult(new ApplyCookiesReport(total, applied, skippedInvalid, skippedErrors, summary));
        }
    }


    private static string ReadJsonValueAsString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var val))
        {
            return string.Empty;
        }

        return val.ValueKind switch
        {
            JsonValueKind.String => val.GetString() ?? string.Empty,
            JsonValueKind.Number => val.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => string.Empty
        };
    }

    /// <summary>
    /// Chromium SQLite: -1 не задано, 0 None, 1 Lax, 2 Strict.
    /// Экспорт из DevTools / антидетект: строки no_restriction, lax, strict, unspecified.
    /// </summary>
    private static CoreWebView2CookieSameSiteKind ParseSameSite(JsonElement el)
    {
        if (!el.TryGetProperty("sameSite", out var ss) || ss.ValueKind == JsonValueKind.Null)
        {
            return CoreWebView2CookieSameSiteKind.Lax;
        }

        if (ss.ValueKind == JsonValueKind.String)
        {
            var s = ss.GetString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s))
            {
                return CoreWebView2CookieSameSiteKind.Lax;
            }

            return s switch
            {
                "no_restriction" or "none" => CoreWebView2CookieSameSiteKind.None,
                "lax" => CoreWebView2CookieSameSiteKind.Lax,
                "strict" => CoreWebView2CookieSameSiteKind.Strict,
                "unspecified" => CoreWebView2CookieSameSiteKind.Lax,
                _ => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? MapSameSiteInt(parsed)
                    : CoreWebView2CookieSameSiteKind.Lax
            };
        }

        if (ss.ValueKind == JsonValueKind.Number && ss.TryGetInt32(out var i))
        {
            return MapSameSiteInt(i);
        }

        return CoreWebView2CookieSameSiteKind.Lax;
    }

    private static CoreWebView2CookieSameSiteKind MapSameSiteInt(int n) =>
        n switch
        {
            0 => CoreWebView2CookieSameSiteKind.None,
            1 => CoreWebView2CookieSameSiteKind.Lax,
            2 => CoreWebView2CookieSameSiteKind.Strict,
            _ => CoreWebView2CookieSameSiteKind.Lax
        };

    /// <summary>ISO-строка, либо Unix time в секундах или миллисекундах (как в экспорте браузеров).</summary>
    private static bool TryParseCookieExpires(JsonElement el, out DateTime expiresUtc)
    {
        expiresUtc = default;
        if (!el.TryGetProperty("expires", out var expEl) || expEl.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (expEl.ValueKind == JsonValueKind.String)
        {
            var expStr = expEl.GetString();
            if (string.IsNullOrWhiteSpace(expStr))
            {
                return false;
            }

            if (DateTimeOffset.TryParse(expStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                expiresUtc = dto.UtcDateTime;
                return true;
            }

            if (long.TryParse(expStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secFromStr))
            {
                return TryUnixToUtc(secFromStr, out expiresUtc);
            }

            return false;
        }

        if (expEl.ValueKind == JsonValueKind.Number)
        {
            if (!expEl.TryGetInt64(out var unix))
            {
                return false;
            }

            return TryUnixToUtc(unix, out expiresUtc);
        }

        return false;
    }

    private static bool TryUnixToUtc(long unix, out DateTime expiresUtc)
    {
        expiresUtc = default;
        if (unix <= 0)
        {
            return false;
        }

        // Миллисекунды, если величина как у JS Date.now() для 2025+
        if (unix > 9999999999L)
        {
            expiresUtc = DateTimeOffset.FromUnixTimeMilliseconds(unix).UtcDateTime;
        }
        else
        {
            expiresUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        }

        return true;
    }
}

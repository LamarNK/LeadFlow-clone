using System;
using System.Linq;
using LeadFlow.Models;

namespace LeadFlow.Services.AntiDetect;

/// <summary>
/// Генератор реалистичных фингерпринтов для аккаунтов.
/// ВАЖНО: Все параметры должны быть консистентными с реальной версией WebView2.
/// </summary>
public static class FingerprintGenerator
{
    /// <summary>
    /// Генерирует реалистичный User-Agent на основе версии WebView2.
    /// НЕ используйте случайные версии браузера — это детектируется!
    /// </summary>
    public static string GenerateUserAgent(string webView2Version)
    {
        var cleanVersion = (webView2Version ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanVersion))
        {
            cleanVersion = "125.0.0.0";
        }

        // GetAvailableBrowserVersionString может возвращать "xxx xxx"; берём первую часть с версией.
        var versionToken = cleanVersion.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var versionParts = versionToken.Split('.');
        if (versionParts.Length < 4)
        {
            versionToken = "125.0.0.0";
        }

        return $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{versionToken} Safari/537.36";
    }

    /// <summary>
    /// Генерирует начальный отпечаток для нового аккаунта по параметрам текущего ПК (без рандомизации).
    /// Вызывайте один раз при первом обращении к профилю и сохраняйте в БД.
    /// </summary>
    public static AccountFingerprint GenerateFingerprint(string webView2Version)
    {
        var resolution = HostFingerprintProvider.GetPrimaryScreenResolution();
        var (viewportW, viewportH) = GetViewportDimensions(resolution);

        return new AccountFingerprint
        {
            UserAgent = GenerateUserAgent(webView2Version),
            ScreenResolution = resolution,
            Timezone = HostFingerprintProvider.GetLocalIanaTimeZoneId(),
            Languages = HostFingerprintProvider.GetAcceptLanguageStyleList(),
            ViewportWidth = viewportW,
            ViewportHeight = viewportH,
            ColorDepth = 24,
            DeviceMemory = null,
            HardwareConcurrency = null
        };
    }

    /// <summary>
    /// Проверяет, что фингерпринт аккаунта реалистичен и консистентен.
    /// </summary>
    public static bool ValidateFingerprint(AvitoAccount account, string actualWebView2Version)
    {
        if (string.IsNullOrWhiteSpace(account.AssignedUserAgent))
            return false;

        // Проверяем, что UA содержит корректную версию браузера
        if (!account.AssignedUserAgent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase))
            return false;

        // Проверяем формат разрешения
        if (!string.IsNullOrWhiteSpace(account.ScreenResolution))
        {
            var parts = account.ScreenResolution.Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h) || w < 800 || h < 600)
                return false;
        }

        // Проверяем формат таймзоны (IANA или UTC)
        if (!string.IsNullOrWhiteSpace(account.Timezone))
        {
            var tz = account.Timezone.Trim();
            if (!tz.Contains('/') && !tz.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!IsLanguageSetValid(account.Languages))
            return false;

        if (!IsPlatformConsistentWithUserAgent(account.AssignedUserAgent, account.NavigatorPlatform))
            return false;

        var overview = FingerprintOverviewState.Parse(account.FingerprintOverviewJson);
        if (overview.DeviceMemoryGb is < 1 or > 32)
            return false;
        if (overview.HardwareConcurrency is < 1 or > 64)
            return false;

        return true;
    }

    /// <summary>
    /// Генерирует реалистичный размер вьюпорта на основе разрешения.
    /// Уменьшает на размер панелей браузера (~100px по вертикали, ~20px по горизонтали).
    /// </summary>
    private static (int Width, int Height) GetViewportDimensions(string resolution)
    {
        var parts = resolution?.Split('x') ?? new[] { "1920", "1080" };
        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h))
            return (1900, 980);

        return (Math.Max(800, w - 20), Math.Max(600, h - 100));
    }

    private static bool IsLanguageSetValid(string? languages)
    {
        if (string.IsNullOrWhiteSpace(languages))
            return false;

        var parts = languages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        return parts.All(static p => p.Length is >= 2 and <= 15);
    }

    private static bool IsPlatformConsistentWithUserAgent(string userAgent, string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return true;

        if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            return platform.Equals("Win32", StringComparison.OrdinalIgnoreCase);
        if (userAgent.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase))
            return platform.Equals("MacIntel", StringComparison.OrdinalIgnoreCase);
        if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
            return platform.StartsWith("Linux", StringComparison.OrdinalIgnoreCase);
        if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase))
            return platform.Equals("iPhone", StringComparison.OrdinalIgnoreCase);
        if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase))
            return platform.StartsWith("Linux", StringComparison.OrdinalIgnoreCase);

        return true;
    }
}

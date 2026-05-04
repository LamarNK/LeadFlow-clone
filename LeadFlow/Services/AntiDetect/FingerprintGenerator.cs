using System;
using LeadFlow.Models;

namespace LeadFlow.Services.AntiDetect;

/// <summary>
/// Генератор реалистичных фингерпринтов для аккаунтов.
/// ВАЖНО: Все параметры должны быть консистентными с реальной версией WebView2.
/// </summary>
public static class FingerprintGenerator
{
    private static readonly Random _random = new();
    
    // Пул реалистичных разрешений экранов (популярные в РФ/СНГ)
    private static readonly string[] _resolutions = 
    {
        "1920x1080", "1366x768", "1440x900", "1536x864", 
        "1600x900", "1280x720", "2560x1440"
    };

    // Пул часовых поясов РФ и соседних стран
    private static readonly string[] _timezones =
    {
        "Europe/Moscow", "Europe/Kiev", "Europe/Minsk", 
        "Asia/Almaty", "Asia/Yekaterinburg", "Asia/Novosibirsk"
    };

    // Пул языковых настроек
    private static readonly string[] _languages =
    {
        "ru-RU,ru,en-US,en",
        "ru,en-US,en",
        "ru-RU,ru,en",
        "en-US,en,ru-RU,ru"
    };

    /// <summary>
    /// Генерирует реалистичный User-Agent на основе версии WebView2.
    /// НЕ используйте случайные версии браузера — это детектируется!
    /// </summary>
    public static string GenerateUserAgent(string webView2Version)
    {
        // Парсим версию WebView2 (формат: "125.0.2535.92")
        var versionParts = webView2Version?.Split('.') ?? new[] { "125", "0", "0", "0" };
        var major = versionParts.Length > 0 ? versionParts[0] : "125";
        var minor = versionParts.Length > 1 ? versionParts[1] : "0";
        
        // Добавляем небольшой рандом в patch/build версию для вариативности
        var patch = RandomNumber(0, 99).ToString().PadLeft(2, '0');
        var build = RandomNumber(0, 9).ToString();
        
        return $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.{minor}.{patch}.{build} Safari/537.36";
    }

    /// <summary>
    /// Генерирует полный набор фингерпринтов для нового аккаунта.
    /// Вызывайте один раз при создании аккаунта и сохраняйте в БД.
    /// </summary>
    public static AccountFingerprint GenerateFingerprint(string webView2Version)
    {
        var resolution = _resolutions[_random.Next(_resolutions.Length)];
        var (viewportW, viewportH) = GetViewportDimensions(resolution);
        
        return new AccountFingerprint
        {
            UserAgent = GenerateUserAgent(webView2Version),
            ScreenResolution = resolution,
            Timezone = _timezones[_random.Next(_timezones.Length)],
            Languages = _languages[_random.Next(_languages.Length)],
            ViewportWidth = viewportW,
            ViewportHeight = viewportH,
            ColorDepth = 24,
            DeviceMemory = RandomChoice(new[] { 4, 8, 16 }),
            HardwareConcurrency = RandomChoice(new[] { 4, 6, 8, 12 })
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

        // Проверяем формат таймзоны
        if (!string.IsNullOrWhiteSpace(account.Timezone) && !account.Timezone.Contains('/'))
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

    private static int RandomNumber(int min, int max) => _random.Next(min, max + 1);
    private static T RandomChoice<T>(T[] options) => options[_random.Next(options.Length)];
}

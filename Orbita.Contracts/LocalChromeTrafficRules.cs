using System.Globalization;

namespace Orbita.Contracts;

/// <summary>
/// Скорость и трафик обычного Chrome. Только мониторинг Local-аккаунтов.
/// AdsPower и Multilogin эти поля не используют.
/// </summary>
public sealed record LocalChromeTrafficSettings(
    string Mode,
    bool BlockMedia,
    bool BlockAnalytics,
    bool BlockImages,
    bool BlockFonts,
    bool BlockPrefetch,
    int NavigationTimeoutSeconds);

public sealed record LocalChromeTrafficLastStats(
    int? FirstNavigationMs,
    int BlockedMedia,
    int BlockedImages,
    int BlockedFonts,
    int BlockedAnalytics,
    int BlockedPrefetch);

public enum LocalChromeTrafficBlockKind
{
    None = 0,
    Media = 1,
    Analytics = 2,
    Image = 3,
    Font = 4,
    Prefetch = 5
}

public static class LocalChromeTrafficRules
{
    public const string ModeNormal = "Normal";
    public const string ModeEconomic = "Economic";
    public const string ModeAggressive = "Aggressive";
    public const string ModeCustom = "Custom";

    public const int DefaultTimeoutSeconds = 60;
    public const int ModeMaxLength = 16;

    public static readonly int[] AllowedTimeouts = [30, 60, 90];

    public static readonly string[] AnalyticsHosts =
    [
        "mc.yandex.ru",
        "top.mail.ru",
        "google-analytics.com",
        "googletagmanager.com",
        "doubleclick.net"
    ];

    private static readonly string[] CaptchaImageHosts =
    [
        "geetest.com",
        "recaptcha.net",
        "gstatic.com",
        "hcaptcha.com",
        "smartcaptcha.yandexcloud.net",
        "captcha.yandex.net"
    ];

    public static LocalChromeTrafficSettings Normal { get; } = new(
        ModeNormal,
        BlockMedia: false,
        BlockAnalytics: false,
        BlockImages: false,
        BlockFonts: false,
        BlockPrefetch: false,
        DefaultTimeoutSeconds);

    public static LocalChromeTrafficSettings Economic { get; } = new(
        ModeEconomic,
        BlockMedia: true,
        BlockAnalytics: true,
        BlockImages: false,
        BlockFonts: false,
        BlockPrefetch: true,
        DefaultTimeoutSeconds);

    public static LocalChromeTrafficSettings Aggressive { get; } = new(
        ModeAggressive,
        BlockMedia: true,
        BlockAnalytics: true,
        BlockImages: true,
        BlockFonts: true,
        BlockPrefetch: true,
        DefaultTimeoutSeconds);

    public static LocalChromeTrafficSettings FromStored(
        string? mode,
        bool blockMedia,
        bool blockAnalytics,
        bool blockImages,
        bool blockFonts,
        bool blockPrefetch,
        int navigationTimeoutSeconds)
    {
        var timeout = AllowedTimeouts.Contains(navigationTimeoutSeconds)
            ? navigationTimeoutSeconds
            : DefaultTimeoutSeconds;
        var flags = new LocalChromeTrafficSettings(
            ModeNormal,
            blockMedia,
            blockAnalytics,
            blockImages,
            blockFonts,
            blockPrefetch,
            timeout);
        return flags with { Mode = ResolveMode(flags) };
    }

    public static LocalChromeTrafficSettings ForPreset(string? mode)
    {
        if (string.Equals(mode, ModeEconomic, StringComparison.OrdinalIgnoreCase))
        {
            return Economic;
        }

        if (string.Equals(mode, ModeAggressive, StringComparison.OrdinalIgnoreCase))
        {
            return Aggressive;
        }

        return Normal;
    }

    public static string ResolveMode(LocalChromeTrafficSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (MatchesPreset(settings, Normal))
        {
            return ModeNormal;
        }

        if (MatchesPreset(settings, Economic))
        {
            return ModeEconomic;
        }

        if (MatchesPreset(settings, Aggressive))
        {
            return ModeAggressive;
        }

        return ModeCustom;
    }

    public static bool TryValidateTimeout(int? seconds, out int normalized, out string? error)
    {
        normalized = DefaultTimeoutSeconds;
        error = null;
        if (seconds is null)
        {
            return true;
        }

        if (!AllowedTimeouts.Contains(seconds.Value))
        {
            error = "Таймаут загрузки: только 30, 60 или 90 секунд.";
            return false;
        }

        normalized = seconds.Value;
        return true;
    }

    public static bool TryApply(
        string? mode,
        bool? blockMedia,
        bool? blockAnalytics,
        bool? blockImages,
        bool? blockFonts,
        bool? blockPrefetch,
        int? navigationTimeoutSeconds,
        LocalChromeTrafficSettings current,
        out LocalChromeTrafficSettings settings,
        out string? error)
    {
        settings = current;
        if (!TryValidateTimeout(navigationTimeoutSeconds, out var timeout, out error))
        {
            return false;
        }

        if (mode is not null
            && !string.Equals(mode, ModeNormal, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, ModeEconomic, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, ModeAggressive, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, ModeCustom, StringComparison.OrdinalIgnoreCase))
        {
            error = "Неизвестный режим скорости и трафика.";
            return false;
        }

        if (IsPresetMode(mode) && !HasExplicitFlagOverrides(
                blockMedia, blockAnalytics, blockImages, blockFonts, blockPrefetch, navigationTimeoutSeconds))
        {
            var preset = ForPreset(mode);
            settings = navigationTimeoutSeconds is null
                ? preset
                : preset with { NavigationTimeoutSeconds = timeout, Mode = ResolveMode(preset with { NavigationTimeoutSeconds = timeout }) };
            return true;
        }

        var candidate = new LocalChromeTrafficSettings(
            ModeNormal,
            blockMedia ?? current.BlockMedia,
            blockAnalytics ?? current.BlockAnalytics,
            blockImages ?? current.BlockImages,
            blockFonts ?? current.BlockFonts,
            blockPrefetch ?? current.BlockPrefetch,
            navigationTimeoutSeconds is null ? current.NavigationTimeoutSeconds : timeout);
        settings = candidate with { Mode = ResolveMode(candidate) };
        return true;
    }

    public static LocalChromeTrafficBlockKind Classify(
        LocalChromeTrafficSettings settings,
        string? resourceType,
        string? url,
        string? purposeHeader,
        string? secPurposeHeader,
        bool captchaActive)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!HasAnyBlock(settings))
        {
            return LocalChromeTrafficBlockKind.None;
        }

        var type = resourceType ?? string.Empty;
        if (IsNeverBlockedCore(type))
        {
            return LocalChromeTrafficBlockKind.None;
        }

        if (settings.BlockAnalytics && IsAnalyticsUrl(url))
        {
            return LocalChromeTrafficBlockKind.Analytics;
        }

        if (IsProtectedAppResource(type))
        {
            return LocalChromeTrafficBlockKind.None;
        }

        if (settings.BlockMedia && IsType(type, "Media"))
        {
            return LocalChromeTrafficBlockKind.Media;
        }

        if (settings.BlockImages
            && IsType(type, "Image")
            && !captchaActive
            && !IsCaptchaImageUrl(url))
        {
            return LocalChromeTrafficBlockKind.Image;
        }

        if (settings.BlockFonts && IsType(type, "Font"))
        {
            return LocalChromeTrafficBlockKind.Font;
        }

        if (settings.BlockPrefetch && IsPrefetchPurpose(purposeHeader, secPurposeHeader))
        {
            return LocalChromeTrafficBlockKind.Prefetch;
        }

        return LocalChromeTrafficBlockKind.None;
    }

    public static bool ShouldBlock(
        LocalChromeTrafficSettings settings,
        string? resourceType,
        string? url,
        string? purposeHeader,
        string? secPurposeHeader,
        bool captchaActive) =>
        Classify(settings, resourceType, url, purposeHeader, secPurposeHeader, captchaActive)
            != LocalChromeTrafficBlockKind.None;

    public static bool IsAnalyticsUrl(string? url)
    {
        if (!TryGetHost(url, out var host))
        {
            return false;
        }

        foreach (var domain in AnalyticsHosts)
        {
            if (HostMatches(host, domain))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsPrefetchPurpose(string? purposeHeader, string? secPurposeHeader) =>
        HasPrefetchToken(purposeHeader) || HasPrefetchToken(secPurposeHeader);

    public static string FormatLastRun(LocalChromeTrafficLastStats? stats)
    {
        if (stats is null)
        {
            return string.Empty;
        }

        return FormatLastRun(
            stats.FirstNavigationMs,
            stats.BlockedMedia,
            stats.BlockedImages,
            stats.BlockedFonts,
            stats.BlockedAnalytics,
            stats.BlockedPrefetch);
    }

    public static string FormatLastRun(
        int? firstNavigationMs,
        int blockedMedia,
        int blockedImages,
        int blockedFonts,
        int blockedAnalytics,
        int blockedPrefetch)
    {
        if (firstNavigationMs is null
            && blockedMedia == 0
            && blockedImages == 0
            && blockedFonts == 0
            && blockedAnalytics == 0
            && blockedPrefetch == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(2);
        if (firstNavigationMs is int ms)
        {
            var seconds = Math.Max(0, ms) / 1000d;
            parts.Add(
                "Последний запуск: "
                + seconds.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"))
                + " с");
        }

        var blocked = new List<string>(5);
        if (blockedMedia > 0)
        {
            blocked.Add("медиа " + blockedMedia.ToString(CultureInfo.InvariantCulture));
        }

        if (blockedImages > 0)
        {
            blocked.Add("изображения " + blockedImages.ToString(CultureInfo.InvariantCulture));
        }

        if (blockedFonts > 0)
        {
            blocked.Add("шрифты " + blockedFonts.ToString(CultureInfo.InvariantCulture));
        }

        if (blockedAnalytics > 0)
        {
            blocked.Add("аналитика " + blockedAnalytics.ToString(CultureInfo.InvariantCulture));
        }

        if (blockedPrefetch > 0)
        {
            blocked.Add("предзагрузка " + blockedPrefetch.ToString(CultureInfo.InvariantCulture));
        }

        parts.Add(blocked.Count == 0
            ? "заблокировано: 0"
            : "заблокировано: " + string.Join(", ", blocked));
        return string.Join(" · ", parts);
    }

    public static LocalChromeTrafficSettings Disabled { get; } = Normal;

    private static bool MatchesPreset(LocalChromeTrafficSettings settings, LocalChromeTrafficSettings preset) =>
        settings.BlockMedia == preset.BlockMedia
        && settings.BlockAnalytics == preset.BlockAnalytics
        && settings.BlockImages == preset.BlockImages
        && settings.BlockFonts == preset.BlockFonts
        && settings.BlockPrefetch == preset.BlockPrefetch
        && settings.NavigationTimeoutSeconds == preset.NavigationTimeoutSeconds;

    private static bool IsPresetMode(string? mode) =>
        string.Equals(mode, ModeNormal, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, ModeEconomic, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, ModeAggressive, StringComparison.OrdinalIgnoreCase);

    private static bool HasExplicitFlagOverrides(
        bool? blockMedia,
        bool? blockAnalytics,
        bool? blockImages,
        bool? blockFonts,
        bool? blockPrefetch,
        int? navigationTimeoutSeconds) =>
        blockMedia is not null
        || blockAnalytics is not null
        || blockImages is not null
        || blockFonts is not null
        || blockPrefetch is not null
        || navigationTimeoutSeconds is not null;

    private static bool HasAnyBlock(LocalChromeTrafficSettings settings) =>
        settings.BlockMedia
        || settings.BlockAnalytics
        || settings.BlockImages
        || settings.BlockFonts
        || settings.BlockPrefetch;

    private static bool IsNeverBlockedCore(string resourceType) =>
        IsType(resourceType, "Document")
        || IsType(resourceType, "Stylesheet")
        || IsType(resourceType, "StyleSheet")
        || IsType(resourceType, "WebSocket")
        || IsType(resourceType, "EventSource");

    private static bool IsProtectedAppResource(string resourceType) =>
        IsType(resourceType, "Script")
        || IsType(resourceType, "Xhr")
        || IsType(resourceType, "XHR")
        || IsType(resourceType, "Fetch");

    private static bool IsType(string resourceType, string expected) =>
        string.Equals(resourceType, expected, StringComparison.OrdinalIgnoreCase);

    private static bool HasPrefetchToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        while (!span.IsEmpty)
        {
            var comma = span.IndexOf(',');
            var token = comma < 0 ? span : span[..comma];
            var semi = token.IndexOf(';');
            if (semi >= 0)
            {
                token = token[..semi];
            }

            if (token.Trim().Equals("prefetch", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (comma < 0)
            {
                break;
            }

            span = span[(comma + 1)..];
        }

        return false;
    }

    private static bool IsCaptchaImageUrl(string? url)
    {
        if (!TryGetHost(url, out var host))
        {
            return false;
        }

        foreach (var domain in CaptchaImageHosts)
        {
            if (HostMatches(host, domain))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetHost(string? url, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        host = uri.Host;
        return true;
    }

    private static bool HostMatches(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}



namespace LeadFlow.Services.AntiDetect;

/// <summary>
/// Собирает <see cref="AccountFingerprint"/> из полей аккаунта для stealth-скриптов.
/// </summary>
public static class AccountFingerprintBuilder
{
    public static AccountFingerprint? FromAccount(AvitoAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.AssignedUserAgent))
        {
            return null;
        }

        var parts = account.ScreenResolution?.Split('x') ?? ["1920", "1080"];
        var width = parts.Length > 0 && int.TryParse(parts[0], out var w) ? w : 1920;
        var height = parts.Length > 1 && int.TryParse(parts[1], out var h) ? h : 1080;

        var spoofGl = account.SpoofWebGl
            && !string.IsNullOrWhiteSpace(account.WebGlVendor)
            && !string.IsNullOrWhiteSpace(account.WebGlRenderer);

        var overview = FingerprintOverviewState.Parse(account.FingerprintOverviewJson);
        int? devMem = overview.RamMode == "Реальный"
            ? null
            : overview.DeviceMemoryGb is >= 1 and <= 32 ? overview.DeviceMemoryGb : 8;
        int? hw = overview.CpuMode == "Реальный"
            ? null
            : overview.HardwareConcurrency is >= 1 and <= 64 ? overview.HardwareConcurrency : 8;
        var ch = ClientHintsSpoof.GetForAccount(account);
        var languages = overview.LanguageMode == "На основе IP"
            ? null
            : account.Languages ?? "ru-RU,ru,en-US,en";
        var uiLanguage = overview.UiLanguageMode switch
        {
            "Реальный" => null,
            "Настроить" => string.IsNullOrWhiteSpace(overview.CustomUiLanguage) ? null : overview.CustomUiLanguage.Trim(),
            _ => languages?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
        };
        bool? doNotTrack = overview.DoNotTrackMode switch
        {
            "По умолчанию" => null,
            "Выключить" => false,
            _ => true
        };

        return new AccountFingerprint
        {
            UserAgent = account.AssignedUserAgent,
            ChPlatform = ch.Platform,
            ChPlatformVersion = ch.PlatformVersion,
            ChMobile = ch.Mobile,
            ScreenResolution = account.ScreenResolution ?? "1920x1080",
            // When enabled, keep browser/native timezone (typically aligned with proxy/IP),
            // otherwise apply manual timezone spoofing value.
            Timezone = account.UseIpTimezone ? null : (account.Timezone ?? "Europe/Moscow"),
            Languages = languages,
            ViewportWidth = width - 20,
            ViewportHeight = height - 100,
            ColorDepth = 24,
            DeviceMemory = devMem,
            HardwareConcurrency = hw,
            NavigatorPlatform = string.IsNullOrWhiteSpace(account.NavigatorPlatform)
                ? DeriveNavigatorPlatform(account)
                : account.NavigatorPlatform.Trim(),
            DoNotTrack = doNotTrack,
            SpoofWebGl = spoofGl,
            WebGlVendor = account.WebGlVendor?.Trim(),
            WebGlRenderer = account.WebGlRenderer?.Trim(),
            CanvasNoise = account.CanvasFingerprintNoise,
            AudioNoise = account.AudioFingerprintNoise,
            AudioNoiseSeedHex = string.IsNullOrWhiteSpace(overview.AudioSeedHex) ? null : overview.AudioSeedHex.Trim(),
            ClientRectsNoiseSeedHex = string.IsNullOrWhiteSpace(overview.ClientRectsSeedHex) ? null : overview.ClientRectsSeedHex.Trim(),
            UiLanguage = uiLanguage,
            GeolocationMode = overview.GeolocationMode,
            GeolocationLatitude = string.IsNullOrWhiteSpace(overview.GeolocationLatitude) ? null : overview.GeolocationLatitude.Trim(),
            GeolocationLongitude = string.IsNullOrWhiteSpace(overview.GeolocationLongitude) ? null : overview.GeolocationLongitude.Trim(),
            GeolocationAccuracyMeters = string.IsNullOrWhiteSpace(overview.GeolocationAccuracyMeters) ? null : overview.GeolocationAccuracyMeters.Trim(),
            MediaDevicesMode = overview.MediaDevicesMode,
            MediaLabel = string.IsNullOrWhiteSpace(overview.MediaLabel) ? null : overview.MediaLabel.Trim(),
            ClientRectsNoise = overview.ClientRectsMode != "Реальный",
            SpeechVoicesMode = overview.SpeechVoicesMode,
            SpeechLabel = string.IsNullOrWhiteSpace(overview.SpeechLabel) ? null : overview.SpeechLabel.Trim(),
            WebGpuMode = overview.WebGpuMode
        };
    }

    private static string DeriveNavigatorPlatform(AvitoAccount a)
    {
        if (a.UseIosOs)
        {
            return "iPhone";
        }

        if (a.UseAndroidOs)
        {
            return "Linux armv8l";
        }

        if (a.UseMacOs)
        {
            return "MacIntel";
        }

        if (a.UseLinuxOs)
        {
            return "Linux x86_64";
        }

        return "Win32";
    }
}

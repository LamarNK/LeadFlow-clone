using LeadFlow.Models;

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
        var devMem = overview.DeviceMemoryGb is >= 1 and <= 32 ? overview.DeviceMemoryGb : 8;
        var hw = overview.HardwareConcurrency is >= 1 and <= 64 ? overview.HardwareConcurrency : 8;

        return new AccountFingerprint
        {
            UserAgent = account.AssignedUserAgent,
            ScreenResolution = account.ScreenResolution ?? "1920x1080",
            Timezone = account.Timezone ?? "Europe/Moscow",
            Languages = account.Languages ?? "ru-RU,ru,en-US,en",
            ViewportWidth = width - 20,
            ViewportHeight = height - 100,
            ColorDepth = 24,
            DeviceMemory = devMem,
            HardwareConcurrency = hw,
            NavigatorPlatform = string.IsNullOrWhiteSpace(account.NavigatorPlatform)
                ? DeriveNavigatorPlatform(account)
                : account.NavigatorPlatform.Trim(),
            DoNotTrack = account.DoNotTrack,
            SpoofWebGl = spoofGl,
            WebGlVendor = account.WebGlVendor?.Trim(),
            WebGlRenderer = account.WebGlRenderer?.Trim(),
            CanvasNoise = account.CanvasFingerprintNoise,
            AudioNoise = account.AudioFingerprintNoise,
            AudioNoiseSeedHex = string.IsNullOrWhiteSpace(overview.AudioSeedHex) ? null : overview.AudioSeedHex.Trim(),
            ClientRectsNoiseSeedHex = string.IsNullOrWhiteSpace(overview.ClientRectsSeedHex) ? null : overview.ClientRectsSeedHex.Trim()
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

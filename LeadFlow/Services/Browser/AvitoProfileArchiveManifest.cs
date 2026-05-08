using LeadFlow.Models;

namespace LeadFlow.Services.Browser;

/// <summary>Корень ZIP: метаданные и снимок настроек аккаунта (без идентичности и путей).</summary>
public sealed class AvitoProfileArchiveManifest
{
    public const string FileName = "LeadFlow.AvitoProfile.json";

    public int FormatVersion { get; set; } = 1;

    public DateTime ExportedAtUtc { get; set; }

    public string? SourceDisplayName { get; set; }

    public AvitoProfileAccountSnapshot? Account { get; set; }
}

/// <summary>Поля аккаунта, связанные с браузерным профилем и антидетектом.</summary>
public sealed class AvitoProfileAccountSnapshot
{
    public string? AvitoResponsesUrl { get; set; }
    public string? AssignedUserAgent { get; set; }
    public string? BrowserName { get; set; }
    public string? BrowserVersion { get; set; }
    public string? UserAgentDevice { get; set; }
    public bool UseWindowsOs { get; set; }
    public string? WindowsVersion { get; set; }
    public bool UseMacOs { get; set; }
    public string? MacOsVersion { get; set; }
    public bool UseLinuxOs { get; set; }
    public string? LinuxVersion { get; set; }
    public bool UseAndroidOs { get; set; }
    public string? AndroidVersion { get; set; }
    public bool UseIosOs { get; set; }
    public string? IosVersion { get; set; }
    public string CookiesJson { get; set; } = "";
    public bool ImportCookiesOnNextStart { get; set; }
    public string Notes { get; set; } = "";
    public string? ScreenResolution { get; set; }
    public bool UseIpTimezone { get; set; }
    public string? Timezone { get; set; }
    public string? Languages { get; set; }
    public string? ProxyAddress { get; set; }
    public string? ProxyType { get; set; }
    public string? ProxyUsername { get; set; }
    public string? ProxyPassword { get; set; }
    public string? ProxyRotationUrl { get; set; }
    public string BrowserLaunchArgs { get; set; } = "";
    public string? NavigatorPlatform { get; set; }
    public bool DoNotTrack { get; set; }
    public string? WebGlVendor { get; set; }
    public string? WebGlRenderer { get; set; }
    public bool SpoofWebGl { get; set; }
    public bool CanvasFingerprintNoise { get; set; }
    public bool AudioFingerprintNoise { get; set; }
    public string? WebRtcLaunchFlags { get; set; }
    public string StartupTabsJson { get; set; } = "[]";
    public string ProxyPresetsJson { get; set; } = "[]";
    public string FingerprintOverviewJson { get; set; } = "{}";

    public static AvitoProfileAccountSnapshot FromAccount(AvitoAccount a) =>
        new()
        {
            AvitoResponsesUrl = a.AvitoResponsesUrl,
            AssignedUserAgent = a.AssignedUserAgent,
            BrowserName = a.BrowserName,
            BrowserVersion = a.BrowserVersion,
            UserAgentDevice = a.UserAgentDevice,
            UseWindowsOs = a.UseWindowsOs,
            WindowsVersion = a.WindowsVersion,
            UseMacOs = a.UseMacOs,
            MacOsVersion = a.MacOsVersion,
            UseLinuxOs = a.UseLinuxOs,
            LinuxVersion = a.LinuxVersion,
            UseAndroidOs = a.UseAndroidOs,
            AndroidVersion = a.AndroidVersion,
            UseIosOs = a.UseIosOs,
            IosVersion = a.IosVersion,
            CookiesJson = a.CookiesJson,
            ImportCookiesOnNextStart = a.ImportCookiesOnNextStart,
            Notes = a.Notes,
            ScreenResolution = a.ScreenResolution,
            UseIpTimezone = a.UseIpTimezone,
            Timezone = a.Timezone,
            Languages = a.Languages,
            ProxyAddress = a.ProxyAddress,
            ProxyType = a.ProxyType,
            ProxyUsername = a.ProxyUsername,
            ProxyPassword = a.ProxyPassword,
            ProxyRotationUrl = a.ProxyRotationUrl,
            BrowserLaunchArgs = a.BrowserLaunchArgs,
            NavigatorPlatform = a.NavigatorPlatform,
            DoNotTrack = a.DoNotTrack,
            WebGlVendor = a.WebGlVendor,
            WebGlRenderer = a.WebGlRenderer,
            SpoofWebGl = a.SpoofWebGl,
            CanvasFingerprintNoise = a.CanvasFingerprintNoise,
            AudioFingerprintNoise = a.AudioFingerprintNoise,
            WebRtcLaunchFlags = a.WebRtcLaunchFlags,
            StartupTabsJson = a.StartupTabsJson,
            ProxyPresetsJson = a.ProxyPresetsJson,
            FingerprintOverviewJson = a.FingerprintOverviewJson,
        };

    public void ApplyTo(AvitoAccount target)
    {
        if (AvitoResponsesUrl is not null)
        {
            target.AvitoResponsesUrl = AvitoResponsesUrl;
        }

        target.AssignedUserAgent = AssignedUserAgent;
        target.BrowserName = BrowserName ?? target.BrowserName;
        target.BrowserVersion = BrowserVersion ?? target.BrowserVersion;
        target.UserAgentDevice = UserAgentDevice ?? target.UserAgentDevice;
        target.UseWindowsOs = UseWindowsOs;
        target.WindowsVersion = WindowsVersion ?? target.WindowsVersion;
        target.UseMacOs = UseMacOs;
        target.MacOsVersion = MacOsVersion ?? target.MacOsVersion;
        target.UseLinuxOs = UseLinuxOs;
        target.LinuxVersion = LinuxVersion ?? target.LinuxVersion;
        target.UseAndroidOs = UseAndroidOs;
        target.AndroidVersion = AndroidVersion ?? target.AndroidVersion;
        target.UseIosOs = UseIosOs;
        target.IosVersion = IosVersion ?? target.IosVersion;
        target.CookiesJson = CookiesJson;
        target.ImportCookiesOnNextStart = ImportCookiesOnNextStart;
        target.Notes = Notes;
        target.ScreenResolution = ScreenResolution;
        target.UseIpTimezone = UseIpTimezone;
        target.Timezone = Timezone;
        target.Languages = Languages;
        target.ProxyAddress = ProxyAddress;
        target.ProxyType = string.IsNullOrWhiteSpace(ProxyType) ? target.ProxyType : ProxyType!;
        target.ProxyUsername = ProxyUsername;
        target.ProxyPassword = ProxyPassword;
        target.ProxyRotationUrl = ProxyRotationUrl;
        target.BrowserLaunchArgs = BrowserLaunchArgs;
        target.NavigatorPlatform = NavigatorPlatform;
        target.DoNotTrack = DoNotTrack;
        target.WebGlVendor = WebGlVendor;
        target.WebGlRenderer = WebGlRenderer;
        target.SpoofWebGl = SpoofWebGl;
        target.CanvasFingerprintNoise = CanvasFingerprintNoise;
        target.AudioFingerprintNoise = AudioFingerprintNoise;
        target.WebRtcLaunchFlags = WebRtcLaunchFlags;
        target.StartupTabsJson = StartupTabsJson;
        target.ProxyPresetsJson = ProxyPresetsJson;
        target.FingerprintOverviewJson = FingerprintOverviewJson;
    }
}

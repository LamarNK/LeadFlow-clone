namespace LeadFlow.Data;

public sealed class AvitoAccountEntity
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string AvitoResponsesUrl { get; set; } = string.Empty;
    public string BrowserProfilePath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? LastAuthCheckAt { get; set; }
    public DateTime? LastMonitoringAt { get; set; }
    public string LastErrorMessage { get; set; } = string.Empty;
    public string BrowserName { get; set; } = string.Empty;
    public string BrowserVersion { get; set; } = "146";
    public string UserAgentDevice { get; set; } = "Все";
    public bool UseWindowsOs { get; set; } = true;
    public string WindowsVersion { get; set; } = "Windows 10";
    public bool UseMacOs { get; set; }
    public string MacOsVersion { get; set; } = "All macOS";
    public bool UseLinuxOs { get; set; }
    public string LinuxVersion { get; set; } = "Linux x86_64";
    public bool UseAndroidOs { get; set; }
    public string AndroidVersion { get; set; } = "All Android";
    public bool UseIosOs { get; set; }
    public string IosVersion { get; set; } = "All iOS";
    public string? AssignedUserAgent { get; set; }
    public string CookiesJson { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int ActiveAdsCount { get; set; }
    public int BlockedCount { get; set; }
    public int DraftsCount { get; set; }
    public DateTime? AdsStatsUpdatedAt { get; set; }
}

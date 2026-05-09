using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Models;
using LeadFlow.Services.AntiDetect;
using LeadFlow.Services.Browser;

namespace LeadFlow.ViewModels;

public partial class AccountSettingsViewModel(
    AvitoAccount account,
    IProfileCookiesService profileCookiesService,
    IProxyCheckService proxyCheckService) : ObservableObject
{
    private const int MaxDisplayNameLen = 100;
    private const int MaxNotesLen = 1500;

    private static readonly JsonSerializerOptions PresetJsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions PresetJsonWriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Random Random = new();
    private readonly AvitoAccount _account = account;
    private readonly IProxyCheckService _proxyCheckService = proxyCheckService;
    private bool _proxyInternalUpdate;
    private bool _uaSyncBusy;
    private bool _fingerprintOverviewHooked;
    private bool _isLoadingAccountSettings;
    private FingerprintOverviewState _fpOverview = FingerprintOverviewState.Parse(account.FingerprintOverviewJson);

    private static readonly (string Vendor, string Renderer)[] WebGlPresetPairs =
    [
        ("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) UHD Graphics 630 Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce GTX 1660 SUPER Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (AMD)", "ANGLE (AMD, AMD Radeon RX 580 Series Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) Iris(R) Xe Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)")
    ];
    /// <summary>
    /// Ключи — пункты комбо (уникальны; два AMD — как в референс-UI: общий и вариант «ПК / Windows»).
    /// </summary>
    private static readonly Dictionary<string, (string Vendor, string[] Renderers)> WebGlPresets = new(StringComparer.Ordinal)
    {
        ["ARM"] = (
            "ARM",
            [
                "Mali-G52 MC2",
                "Mali-G57 MC2",
                "Mali-G68 MC4",
                "Mali-G710 MC10",
                "Mali-G78 MC14"
            ]),
        ["Qualcomm"] = (
            "Qualcomm",
            [
                "Adreno (TM) 640",
                "Adreno (TM) 650",
                "Adreno (TM) 660",
                "Adreno (TM) 730",
                "Adreno (TM) 740"
            ]),
        ["Apple Inc."] = (
            "Apple Inc.",
            [
                "Apple GPU",
                "Apple M1",
                "Apple M1 Pro",
                "Apple M2",
                "Apple M3"
            ]),
        ["Google Inc. (AMD)"] = (
            "Google Inc. (AMD)",
            [
                "ANGLE (AMD, AMD Radeon RX 580 Series Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (AMD, AMD Radeon RX 6600 XT Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (AMD, AMD Radeon RX 6700 XT Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (AMD, AMD Radeon(TM) Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)"
            ]),
        ["Google Inc. (Intel)"] = (
            "Google Inc. (Intel)",
            [
                "ANGLE (Intel, Intel(R) UHD Graphics 630 Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (Intel, Intel(R) UHD Graphics 770 Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (Intel, Intel(R) UHD Graphics 730 Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (Intel, Intel(R) Iris(R) Xe Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)"
            ]),
        ["Google Inc. (Apple)"] = (
            "Google Inc. (Apple)",
            [
                "ANGLE (Apple, Apple M1, Unspecified Version)",
                "ANGLE (Apple, Apple M2, Unspecified Version)",
                "ANGLE (Apple, Apple M3, Unspecified Version)",
                "ANGLE (Apple, ANGLE Metal Renderer: Apple M1 Pro, Unspecified Version)"
            ]),
        ["Google Inc. (Intel Inc.)"] = (
            "Google Inc. (Intel Inc.)",
            [
                "ANGLE (Intel Inc., Intel(R) Iris(R) Plus Graphics 655, Metal 88.1)",
                "ANGLE (Intel Inc., Intel(R) UHD Graphics 630, Metal 88.1)",
                "ANGLE (Intel Inc., Intel(R) Iris(R) Xe Graphics, Metal 88.1)"
            ]),
        ["Google Inc. (AMD) — Windows"] = (
            "Google Inc. (AMD)",
            [
                "ANGLE (AMD, AMD Radeon RX 7900 XT Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (AMD, AMD Radeon RX 7800 XT Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (AMD, AMD Radeon RX 7600 Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (AMD, AMD Radeon RX 5700 XT Direct3D11 vs_5_0 ps_5_0, D3D11)"
            ]),
        ["Google Inc. (NVIDIA)"] = (
            "Google Inc. (NVIDIA)",
            [
                "ANGLE (NVIDIA, NVIDIA GeForce GTX 1660 SUPER Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (NVIDIA, NVIDIA GeForce RTX 4060 Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (NVIDIA, NVIDIA GeForce RTX 4070 Direct3D11 vs_5_0 ps_5_0, D3D11)"
            ]),
        ["Others"] = (
            "Google Inc. (Google)",
            [
                "ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero) (0x0000C0DE)), SwiftShader driver)",
                "ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device), SwiftShader driver)",
                "ANGLE (Google, OpenGL ES 3.2 (ANGLE 2.1.19065 git hash), SwiftShader)"
            ])
    };

    private static readonly HashSet<string> FingerprintOverviewSourceProps =
    [
        nameof(Languages),
        nameof(ScreenResolution),
        nameof(AssignedUserAgent),
        nameof(CanvasFingerprintNoise),
        nameof(AudioFingerprintNoise),
        nameof(SpoofWebGl),
        nameof(WebGlVendor),
        nameof(WebGlRenderer),
        nameof(DoNotTrack),
        nameof(WebRtcLaunchFlags),
        nameof(NavigatorPlatform),
        nameof(UseWindowsOs),
        nameof(UseMacOs),
        nameof(UseLinuxOs),
        nameof(UseAndroidOs),
        nameof(UseIosOs)
    ];

    public string CurrentBrowserName => BrowserVersionProvider.CurrentBrowserName;
    public string CurrentBrowserVersion => BrowserVersionProvider.GetCurrentChromiumMajorString();

    public IReadOnlyList<string> WindowsVersionOptions { get; } = ["All Windows", "Windows 11", "Windows 10", "Windows 8", "Windows 7"];
    public IReadOnlyList<string> MacOsVersionOptions { get; } = ["All macOS", "macOS 26", "macOS 15", "macOS 14", "macOS 13", "macOS 12", "macOS 11", "macOS 10"];
    public IReadOnlyList<string> LinuxVersionOptions { get; } = ["Linux x86_64", "Ubuntu", "Debian", "Fedora"];
    public IReadOnlyList<string> AndroidVersionOptions { get; } = ["All Android", "Android 15", "Android 14", "Android 13", "Android 12", "Android 11", "Android 10", "Android 9"];
    public IReadOnlyList<string> IosVersionOptions { get; } = ["All iOS", "iOS 18", "iOS 17", "iOS 16", "iOS 15"];

    public IReadOnlyList<string> ProxyTypeOptions { get; } = ["http", "socks5"];
    public IReadOnlyList<string> WebRtcModeOptions { get; } = ["Переадресация", "Подмена", "Реальный", "Отключить", "Прокси UDP"];
    public IReadOnlyList<string> TimezoneModeOptions { get; } = ["На основе IP", "Настроить"];
    public IReadOnlyList<string> GeolocationModeOptions { get; } = ["На основе IP", "Настроить", "Блокировать"];
    public IReadOnlyList<string> LanguageModeOptions { get; } = ["На основе IP", "Настроить"];
    public IReadOnlyList<string> UiLanguageModeOptions { get; } = ["На основе языка", "Реальный", "Настроить"];
    public IReadOnlyList<string> ScreenModeOptions { get; } = ["Предопределенный", "Настроить", "На основе User-Agent"];
    public IReadOnlyList<string> FontsModeOptions { get; } = ["По умолчанию", "Настроить"];
    public IReadOnlyList<string> NoiseModeOptions { get; } = ["Шум", "Реальный"];
    public IReadOnlyList<string> WebGlModeOptions { get; } = ["Подмена", "Реальный"];
    public IReadOnlyList<string> AutoOrCustomModeOptions { get; } = ["[Auto]", "Настроить", "Реальный"];
    public IReadOnlyList<string> RealOrCustomModeOptions { get; } = ["Реальный", "Настроить"];
    public IReadOnlyList<string> WebGpuModeOptions { get; } = ["На основе WebGL", "Реальный", "Отключить"];
    public IReadOnlyList<string> DntModeOptions { get; } = ["По умолчанию", "Включить", "Выключить"];
    public IReadOnlyList<string> ToggleModeOptions { get; } = ["Включить", "Выключить"];
    public IReadOnlyList<string> HardwareAccelerationModeOptions { get; } = ["По умолчанию", "Включить", "Выключить"];
    public IReadOnlyList<string> WebGlPresetOptions { get; } =
    [
        "ARM",
        "Qualcomm",
        "Apple Inc.",
        "Google Inc. (AMD)",
        "Google Inc. (Intel)",
        "Google Inc. (Apple)",
        "Google Inc. (Intel Inc.)",
        "Google Inc. (AMD) — Windows",
        "Google Inc. (NVIDIA)",
        "Others"
    ];

    public ObservableCollection<UaPresetRow> UaPresetRows { get; } = [];

    [ObservableProperty] private bool isUaPresetDropdownOpen;
    [ObservableProperty] private string displayName = account.DisplayName;
    [ObservableProperty] private string browserName = BrowserVersionProvider.CurrentBrowserName;
    [ObservableProperty] private string browserVersion = ResolveInitialBrowserVersion(account);
    [ObservableProperty] private bool useWindowsOs = account.UseWindowsOs;
    [ObservableProperty] private string windowsVersion = string.IsNullOrWhiteSpace(account.WindowsVersion) ? "Windows 10" : account.WindowsVersion;
    [ObservableProperty] private bool useMacOs = account.UseMacOs;
    [ObservableProperty] private string macOsVersion = string.IsNullOrWhiteSpace(account.MacOsVersion) ? "All macOS" : account.MacOsVersion;
    [ObservableProperty] private bool useLinuxOs = account.UseLinuxOs;
    [ObservableProperty] private string linuxVersion = string.IsNullOrWhiteSpace(account.LinuxVersion) ? "Linux x86_64" : account.LinuxVersion;
    [ObservableProperty] private bool useAndroidOs = account.UseAndroidOs;
    [ObservableProperty] private string androidVersion = string.IsNullOrWhiteSpace(account.AndroidVersion) ? "All Android" : account.AndroidVersion;
    [ObservableProperty] private bool useIosOs = account.UseIosOs;
    [ObservableProperty] private string iosVersion = string.IsNullOrWhiteSpace(account.IosVersion) ? "All iOS" : account.IosVersion;
    [ObservableProperty] private string userAgentDevice = string.IsNullOrWhiteSpace(account.UserAgentDevice) ? "Все" : account.UserAgentDevice;
    [ObservableProperty] private string? assignedUserAgent = account.AssignedUserAgent;
    [ObservableProperty] private string cookiesJson = account.CookiesJson;
    [ObservableProperty] private bool importCookiesOnNextStart = account.ImportCookiesOnNextStart;
    [ObservableProperty] private string notes = account.Notes;
    [ObservableProperty] private string? proxyAddress = string.IsNullOrWhiteSpace(account.ProxyAddress) ? null : account.ProxyAddress.Trim();
    [ObservableProperty] private string proxyHost = ParseProxyAddressForUi(string.IsNullOrWhiteSpace(account.ProxyAddress) ? null : account.ProxyAddress.Trim()).Host;
    [ObservableProperty] private string proxyPort = ParseProxyAddressForUi(string.IsNullOrWhiteSpace(account.ProxyAddress) ? null : account.ProxyAddress.Trim()).Port;
    [ObservableProperty] private string proxyType = NormalizeProxyType(account.ProxyType);
    [ObservableProperty] private string screenResolution = string.IsNullOrWhiteSpace(account.ScreenResolution)
        ? HostFingerprintProvider.GetPrimaryScreenResolution()
        : account.ScreenResolution!;
    [ObservableProperty] private bool useIpTimezone = account.UseIpTimezone;
    [ObservableProperty] private string timezone = string.IsNullOrWhiteSpace(account.Timezone)
        ? HostFingerprintProvider.GetLocalIanaTimeZoneId()
        : account.Timezone!;
    [ObservableProperty] private string languages = string.IsNullOrWhiteSpace(account.Languages)
        ? HostFingerprintProvider.GetAcceptLanguageStyleList()
        : account.Languages!;
    [ObservableProperty] private string? proxyUsername = account.ProxyUsername;
    [ObservableProperty] private string? proxyPassword = account.ProxyPassword;
    [ObservableProperty] private string? proxyRotationUrl = account.ProxyRotationUrl;
    [ObservableProperty] private bool isProxyChecking;
    [ObservableProperty] private string proxyCheckResultText = "";
    [ObservableProperty] private bool proxyCheckIsError;
    [ObservableProperty] private string browserLaunchArgs = account.BrowserLaunchArgs ?? "";
    [ObservableProperty] private string? navigatorPlatform = account.NavigatorPlatform;
    [ObservableProperty] private bool doNotTrack = account.DoNotTrack;
    [ObservableProperty] private bool spoofWebGl = account.SpoofWebGl;
    [ObservableProperty] private string selectedWebGlPreset = "Google Inc. (Intel)";
    [ObservableProperty] private string? webGlVendor = account.WebGlVendor;
    [ObservableProperty] private string? webGlRenderer = account.WebGlRenderer;
    [ObservableProperty] private bool canvasFingerprintNoise = account.CanvasFingerprintNoise;
    [ObservableProperty] private bool audioFingerprintNoise = account.AudioFingerprintNoise;
    [ObservableProperty] private string? webRtcLaunchFlags = account.WebRtcLaunchFlags;
    [ObservableProperty] private string statusHint = "";

    public ObservableCollection<ProxyPresetRowViewModel> ProxyPresets { get; } = [];

    public bool ShowProxyCheckFeedback => IsProxyChecking || !string.IsNullOrWhiteSpace(ProxyCheckResultText);

    public string DisplayNameCounter => $"{DisplayName.Length} / {MaxDisplayNameLen}";

    public string NotesCounter => $"{Notes.Length} / {MaxNotesLen}";

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(DisplayNameCounter));

    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(NotesCounter));
    partial void OnScreenResolutionChanged(string value)
    {
        _fpOverview.ScreenFollowsUa = false;
        if (ScreenMode == "На основе User-Agent")
        {
            ScreenMode = "Настроить";
        }
    }
    partial void OnUseIpTimezoneChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowManualTimezoneField));
        OnPropertyChanged(nameof(TimezoneMode));
    }
    partial void OnSelectedWebGlPresetChanged(string value)
    {
        if (_isLoadingAccountSettings)
        {
            return;
        }

        ApplySelectedWebGlPreset();
    }

    partial void OnIsProxyCheckingChanged(bool value)
    {
        CheckProxyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowProxyCheckFeedback));
    }

    partial void OnProxyCheckResultTextChanged(string value) => OnPropertyChanged(nameof(ShowProxyCheckFeedback));

    partial void OnProxyAddressChanged(string? value)
    {
        if (_proxyInternalUpdate)
        {
            CheckProxyCommand.NotifyCanExecuteChanged();
            return;
        }

        _proxyInternalUpdate = true;
        try
        {
            var (h, p) = ParseProxyAddressForUi(value);
            ProxyHost = h;
            ProxyPort = p;
        }
        finally
        {
            _proxyInternalUpdate = false;
        }

        CheckProxyCommand.NotifyCanExecuteChanged();
    }

    partial void OnProxyHostChanged(string value)
    {
        if (_proxyInternalUpdate)
        {
            return;
        }

        RecombineProxyAddressFromHostPort();
    }

    partial void OnProxyPortChanged(string value)
    {
        if (_proxyInternalUpdate)
        {
            return;
        }

        RecombineProxyAddressFromHostPort();
    }

    public string WebRtcMode
    {
        get => GetFpString(_fpOverview.WebRtcMode, "Реальный");
        set => SetFpString(_fpOverview.WebRtcMode, value, "Реальный", nameof(WebRtcMode), v => _fpOverview.WebRtcMode = v);
    }

    public string TimezoneMode
    {
        get => UseIpTimezone ? "На основе IP" : "Настроить";
        set
        {
            var useIp = value == "На основе IP";
            if (UseIpTimezone != useIp)
            {
                UseIpTimezone = useIp;
            }
            else
            {
                OnPropertyChanged(nameof(TimezoneMode));
                OnPropertyChanged(nameof(ShowManualTimezoneField));
            }
        }
    }

    public string GeolocationMode
    {
        get => GetFpString(_fpOverview.GeolocationMode, "На основе IP");
        set
        {
            SetFpString(_fpOverview.GeolocationMode, value, "На основе IP", nameof(GeolocationMode), v => _fpOverview.GeolocationMode = v);
            OnPropertyChanged(nameof(ShowCustomGeolocationFields));
        }
    }

    public string LanguageMode
    {
        get => GetFpString(_fpOverview.LanguageMode, "На основе IP");
        set => SetFpString(_fpOverview.LanguageMode, value, "На основе IP", nameof(LanguageMode), v => _fpOverview.LanguageMode = v);
    }

    public string UiLanguageMode
    {
        get => GetFpString(_fpOverview.UiLanguageMode, "Реальный");
        set
        {
            SetFpString(_fpOverview.UiLanguageMode, value, "Реальный", nameof(UiLanguageMode), v => _fpOverview.UiLanguageMode = v);
            OnPropertyChanged(nameof(ShowCustomUiLanguageField));
        }
    }

    public string CustomUiLanguage
    {
        get => _fpOverview.CustomUiLanguage;
        set => SetFpString(_fpOverview.CustomUiLanguage, value, string.Empty, nameof(CustomUiLanguage), v => _fpOverview.CustomUiLanguage = v);
    }

    public string ScreenMode
    {
        get
        {
            if (_fpOverview.ScreenFollowsUa)
            {
                return "На основе User-Agent";
            }

            var mode = GetFpString(_fpOverview.ScreenMode, "На основе User-Agent");
            return mode == "На основе User-Agent" ? "Настроить" : mode;
        }
        set
        {
            var normalized = value == "Предопределенный" ? "Настроить" : value;
            if (_fpOverview.ScreenFollowsUa == (normalized == "На основе User-Agent")
                && string.Equals(_fpOverview.ScreenMode, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _fpOverview.ScreenFollowsUa = normalized == "На основе User-Agent";
            _fpOverview.ScreenMode = normalized;
            OnPropertyChanged(nameof(ScreenMode));
            OnPropertyChanged(nameof(ShowScreenResolutionField));
            RefreshFingerprintOverview();
        }
    }

    public string FontsMode
    {
        get => GetFpString(_fpOverview.FontsMode, "По умолчанию");
        set
        {
            SetFpString(_fpOverview.FontsMode, value, "По умолчанию", nameof(FontsMode), v => _fpOverview.FontsMode = v);
            OnPropertyChanged(nameof(ShowCustomFontsField));
        }
    }

    public string CustomFonts
    {
        get => _fpOverview.CustomFonts;
        set => SetFpString(_fpOverview.CustomFonts, value, string.Empty, nameof(CustomFonts), v => _fpOverview.CustomFonts = v);
    }

    public string CanvasMode
    {
        get => CanvasFingerprintNoise ? "Шум" : "Реальный";
        set
        {
            var noise = value == "Шум";
            if (CanvasFingerprintNoise == noise)
            {
                return;
            }

            CanvasFingerprintNoise = noise;
            OnPropertyChanged(nameof(CanvasMode));
        }
    }

    public string WebGlImageMode
    {
        get => SpoofWebGl ? "Подмена" : "Реальный";
        set
        {
            var spoof = value == "Подмена";
            if (SpoofWebGl == spoof)
            {
                return;
            }

            SpoofWebGl = spoof;
            OnPropertyChanged(nameof(WebGlImageMode));
            OnPropertyChanged(nameof(WebGlMetadataMode));
            OnPropertyChanged(nameof(ShowWebGlConfigFields));
        }
    }

    public string AudioMode
    {
        get => AudioFingerprintNoise ? "Шум" : "Реальный";
        set
        {
            var noise = value == "Шум";
            if (AudioFingerprintNoise == noise)
            {
                return;
            }

            AudioFingerprintNoise = noise;
            OnPropertyChanged(nameof(AudioMode));
        }
    }

    public string MediaDevicesMode
    {
        get => GetFpString(_fpOverview.MediaDevicesMode, "Реальный");
        set
        {
            SetFpString(_fpOverview.MediaDevicesMode, value, "Реальный", nameof(MediaDevicesMode), v => _fpOverview.MediaDevicesMode = v);
            OnPropertyChanged(nameof(ShowCustomMediaDevicesField));
        }
    }

    public string MediaDevicesLabel
    {
        get => _fpOverview.MediaLabel;
        set => SetFpString(_fpOverview.MediaLabel, value, "Auto", nameof(MediaDevicesLabel), v => _fpOverview.MediaLabel = v);
    }

    public string ClientRectsMode
    {
        get => GetFpString(_fpOverview.ClientRectsMode, "Реальный");
        set => SetFpString(_fpOverview.ClientRectsMode, value, "Реальный", nameof(ClientRectsMode), v => _fpOverview.ClientRectsMode = v);
    }

    public string SpeechVoicesMode
    {
        get => GetFpString(_fpOverview.SpeechVoicesMode, "Реальный");
        set
        {
            SetFpString(_fpOverview.SpeechVoicesMode, value, "Реальный", nameof(SpeechVoicesMode), v => _fpOverview.SpeechVoicesMode = v);
            OnPropertyChanged(nameof(ShowCustomSpeechVoicesField));
        }
    }

    public string SpeechVoicesLabel
    {
        get => _fpOverview.SpeechLabel;
        set => SetFpString(_fpOverview.SpeechLabel, value, "Auto", nameof(SpeechVoicesLabel), v => _fpOverview.SpeechLabel = v);
    }

    public string WebGlMetadataMode
    {
        get => SpoofWebGl ? "Настроить" : "Реальный";
        set
        {
            var spoof = value == "Настроить";
            if (SpoofWebGl == spoof)
            {
                return;
            }

            SpoofWebGl = spoof;
            OnPropertyChanged(nameof(WebGlMetadataMode));
            OnPropertyChanged(nameof(WebGlImageMode));
            OnPropertyChanged(nameof(ShowWebGlConfigFields));
        }
    }

    public string WebGpuMode
    {
        get => GetFpString(_fpOverview.WebGpuMode, "Реальный");
        set => SetFpString(_fpOverview.WebGpuMode, value, "Реальный", nameof(WebGpuMode), v => _fpOverview.WebGpuMode = v);
    }

    public string CpuMode
    {
        get => GetFpString(_fpOverview.CpuMode, "Реальный");
        set
        {
            SetFpString(_fpOverview.CpuMode, value, "Реальный", nameof(CpuMode), v => _fpOverview.CpuMode = v);
            OnPropertyChanged(nameof(ShowCustomCpuField));
        }
    }

    public string RamMode
    {
        get => GetFpString(_fpOverview.RamMode, "Реальный");
        set
        {
            SetFpString(_fpOverview.RamMode, value, "Реальный", nameof(RamMode), v => _fpOverview.RamMode = v);
            OnPropertyChanged(nameof(ShowCustomRamField));
        }
    }

    public string DeviceNameMode
    {
        get => GetFpString(_fpOverview.DeviceNameMode, "Реальный");
        set
        {
            SetFpString(_fpOverview.DeviceNameMode, value, "Реальный", nameof(DeviceNameMode), v => _fpOverview.DeviceNameMode = v);
            OnPropertyChanged(nameof(ShowCustomDeviceNameField));
        }
    }

    public string MacAddressMode
    {
        get => GetFpString(_fpOverview.MacAddressMode, "Реальный");
        set
        {
            SetFpString(_fpOverview.MacAddressMode, value, "Реальный", nameof(MacAddressMode), v => _fpOverview.MacAddressMode = v);
            OnPropertyChanged(nameof(ShowCustomMacField));
        }
    }

    public string DoNotTrackMode
    {
        get => GetFpString(_fpOverview.DoNotTrackMode, "По умолчанию");
        set => SetFpString(_fpOverview.DoNotTrackMode, value, "По умолчанию", nameof(DoNotTrackMode), v => _fpOverview.DoNotTrackMode = v);
    }

    public bool PortScanProtectionEnabled
    {
        get => _fpOverview.PortScanProtectionEnabled;
        set
        {
            if (_fpOverview.PortScanProtectionEnabled == value)
            {
                return;
            }

            _fpOverview.PortScanProtectionEnabled = value;
            OnPropertyChanged(nameof(PortScanProtectionEnabled));
            OnPropertyChanged(nameof(ShowAllowedPortScanPortsField));
            RefreshFingerprintOverview();
        }
    }

    public string AllowedPortScanPorts
    {
        get => _fpOverview.AllowedPortScanPorts;
        set => SetFpString(_fpOverview.AllowedPortScanPorts, value, string.Empty, nameof(AllowedPortScanPorts), v => _fpOverview.AllowedPortScanPorts = v);
    }

    public string HardwareAccelerationMode
    {
        get => GetFpString(_fpOverview.HardwareAccelerationMode, "По умолчанию");
        set => SetFpString(_fpOverview.HardwareAccelerationMode, value, "По умолчанию", nameof(HardwareAccelerationMode), v => _fpOverview.HardwareAccelerationMode = v);
    }

    public bool DisableTlsFeatures
    {
        get => _fpOverview.DisableTlsFeatures;
        set
        {
            if (_fpOverview.DisableTlsFeatures == value)
            {
                return;
            }

            _fpOverview.DisableTlsFeatures = value;
            OnPropertyChanged(nameof(DisableTlsFeatures));
            RefreshFingerprintOverview();
        }
    }

    public string GeolocationLatitude
    {
        get => _fpOverview.GeolocationLatitude;
        set => SetFpString(_fpOverview.GeolocationLatitude, value, string.Empty, nameof(GeolocationLatitude), v => _fpOverview.GeolocationLatitude = v);
    }

    public string GeolocationLongitude
    {
        get => _fpOverview.GeolocationLongitude;
        set => SetFpString(_fpOverview.GeolocationLongitude, value, string.Empty, nameof(GeolocationLongitude), v => _fpOverview.GeolocationLongitude = v);
    }

    public string GeolocationAccuracyMeters
    {
        get => _fpOverview.GeolocationAccuracyMeters;
        set => SetFpString(_fpOverview.GeolocationAccuracyMeters, value, string.Empty, nameof(GeolocationAccuracyMeters), v => _fpOverview.GeolocationAccuracyMeters = v);
    }

    public int HardwareConcurrencyValue
    {
        get => _fpOverview.HardwareConcurrency;
        set => SetFpInt(_fpOverview.HardwareConcurrency, value, nameof(HardwareConcurrencyValue), v => _fpOverview.HardwareConcurrency = v);
    }

    public int DeviceMemoryValue
    {
        get => _fpOverview.DeviceMemoryGb;
        set => SetFpInt(_fpOverview.DeviceMemoryGb, value, nameof(DeviceMemoryValue), v => _fpOverview.DeviceMemoryGb = v);
    }

    public string DeviceNameValue
    {
        get => _fpOverview.DeviceName;
        set => SetFpString(_fpOverview.DeviceName, value, string.Empty, nameof(DeviceNameValue), v => _fpOverview.DeviceName = v);
    }

    public string MacAddressValue
    {
        get => _fpOverview.MacAddress;
        set => SetFpString(_fpOverview.MacAddress, value, string.Empty, nameof(MacAddressValue), v => _fpOverview.MacAddress = v);
    }

    public bool ShowCustomGeolocationFields => GeolocationMode == "Настроить";
    public bool ShowCustomUiLanguageField => UiLanguageMode == "Настроить";
    public bool ShowCustomFontsField => FontsMode == "Настроить";
    public bool ShowScreenResolutionField => ScreenMode != "На основе User-Agent";
    public bool ShowCustomMediaDevicesField => MediaDevicesMode == "Настроить";
    public bool ShowCustomSpeechVoicesField => SpeechVoicesMode == "Настроить";
    public bool ShowWebGlConfigFields => SpoofWebGl;
    public bool ShowCustomCpuField => CpuMode == "Настроить";
    public bool ShowCustomRamField => RamMode == "Настроить";
    public bool ShowCustomDeviceNameField => DeviceNameMode == "Настроить";
    public bool ShowCustomMacField => MacAddressMode == "Настроить";
    public bool ShowAllowedPortScanPortsField => PortScanProtectionEnabled;

    private string GetFpString(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private void SetFpString(string backingField, string? value, string fallback, string propertyName, Action<string> assign)
    {
        var next = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (string.Equals(backingField, next, StringComparison.Ordinal))
        {
            return;
        }

        assign(next);
        OnPropertyChanged(propertyName);
        RefreshFingerprintOverview();
    }

    private void SetFpInt(int backingField, int value, string propertyName, Action<int> assign)
    {
        if (backingField == value)
        {
            return;
        }

        assign(value);
        OnPropertyChanged(propertyName);
        RefreshFingerprintOverview();
    }

    public string OverviewLanguageText => UiLanguageMode switch
    {
        "Реальный" => "Реальный",
        "Настроить" => string.IsNullOrWhiteSpace(CustomUiLanguage) ? "Настроить" : CustomUiLanguage,
        _ => "На основе языка"
    };

    public string OverviewScreenText =>
        ScreenMode == "На основе User-Agent" && !string.IsNullOrWhiteSpace(AssignedUserAgent)
            ? "На основе User-Agent"
            : (string.IsNullOrWhiteSpace(ScreenResolution) ? "—" : ScreenResolution);

    public bool ShowManualTimezoneField => !UseIpTimezone;

    public string OverviewFontsText =>
        FontsMode == "Настроить"
            ? (string.IsNullOrWhiteSpace(CustomFonts) ? "Настроить" : CustomFonts)
            : "По умолчанию";

    public string OverviewCanvasText => CanvasFingerprintNoise ? "Шум" : "Реальный";

    public string OverviewWebGlImageText =>
        SpoofWebGl && !string.IsNullOrWhiteSpace(WebGlVendor) ? "Подмена" : "Реальный";

    public string OverviewAudioText => AudioFingerprintNoise
        ? (string.IsNullOrWhiteSpace(_fpOverview.AudioSeedHex) ? "Шум" : $"Шум [{_fpOverview.AudioSeedHex}]")
        : "Реальный";

    public string OverviewMediaDevicesText =>
        MediaDevicesMode == "Реальный"
            ? "Реальный"
            : MediaDevicesMode == "Настроить"
                ? (string.IsNullOrWhiteSpace(MediaDevicesLabel) ? "Настроить" : MediaDevicesLabel)
                : $"Шум [{_fpOverview.MediaLabel}]";

    public string OverviewClientRectsText =>
        ClientRectsMode == "Реальный"
            ? "Реальный"
            : string.IsNullOrWhiteSpace(_fpOverview.ClientRectsSeedHex)
                ? "Шум"
                : $"Шум [{_fpOverview.ClientRectsSeedHex}]";

    public string OverviewSpeechVoicesText =>
        SpeechVoicesMode == "Реальный"
            ? "Реальный"
            : SpeechVoicesMode == "Настроить"
                ? (string.IsNullOrWhiteSpace(SpeechVoicesLabel) ? "Настроить" : SpeechVoicesLabel)
                : $"Шум [{_fpOverview.SpeechLabel}]";

    public string OverviewWebGlMetaText
    {
        get
        {
            if (!SpoofWebGl || string.IsNullOrWhiteSpace(WebGlVendor))
            {
                return "Реальный (без подмены WebGL)";
            }

            var r = WebGlRenderer ?? "";
            return string.IsNullOrWhiteSpace(r) ? WebGlVendor! : $"{WebGlVendor} — {r}";
        }
    }

    public string OverviewWebGpuText => WebGpuMode;

    public string OverviewCpuText =>
        CpuMode == "Реальный"
            ? "Реальный"
            : _fpOverview.HardwareConcurrency > 0 ? $"{_fpOverview.HardwareConcurrency} cores" : "—";

    public string OverviewRamText =>
        RamMode == "Реальный"
            ? "Реальный"
            : _fpOverview.DeviceMemoryGb > 0 ? $"{_fpOverview.DeviceMemoryGb} GB" : "—";

    public string OverviewDeviceNameText =>
        DeviceNameMode == "Реальный"
            ? "Реальный"
            : string.IsNullOrWhiteSpace(_fpOverview.DeviceName) ? "—" : _fpOverview.DeviceName;

    public string OverviewMacText =>
        MacAddressMode == "Реальный"
            ? "Реальный"
            : string.IsNullOrWhiteSpace(_fpOverview.MacAddress) ? "—" : _fpOverview.MacAddress;

    public string OverviewDntText => DoNotTrackMode;

    public string OverviewWebRtcText =>
        string.IsNullOrWhiteSpace(WebRtcLaunchFlags) ? WebRtcMode : $"{WebRtcMode} + флаги";

    public string OverviewPlatformText =>
        string.IsNullOrWhiteSpace(NavigatorPlatform) ? "(из системы UA слева)" : NavigatorPlatform!;

    private void OnPropertyChangedForFingerprintOverview(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || e.PropertyName.StartsWith("Overview", StringComparison.Ordinal))
        {
            return;
        }

        if (FingerprintOverviewSourceProps.Contains(e.PropertyName))
        {
            RefreshFingerprintOverview();
        }
    }

    private void RefreshFingerprintOverview()
    {
        OnPropertyChanged(nameof(OverviewLanguageText));
        OnPropertyChanged(nameof(OverviewScreenText));
        OnPropertyChanged(nameof(OverviewFontsText));
        OnPropertyChanged(nameof(OverviewCanvasText));
        OnPropertyChanged(nameof(OverviewWebGlImageText));
        OnPropertyChanged(nameof(OverviewAudioText));
        OnPropertyChanged(nameof(OverviewMediaDevicesText));
        OnPropertyChanged(nameof(OverviewClientRectsText));
        OnPropertyChanged(nameof(OverviewSpeechVoicesText));
        OnPropertyChanged(nameof(OverviewWebGlMetaText));
        OnPropertyChanged(nameof(OverviewWebGpuText));
        OnPropertyChanged(nameof(OverviewCpuText));
        OnPropertyChanged(nameof(OverviewRamText));
        OnPropertyChanged(nameof(OverviewDeviceNameText));
        OnPropertyChanged(nameof(OverviewMacText));
        OnPropertyChanged(nameof(OverviewDntText));
        OnPropertyChanged(nameof(OverviewWebRtcText));
        OnPropertyChanged(nameof(OverviewPlatformText));
    }

    [RelayCommand]
    private void RegenerateNewFingerprint()
    {
        RegenerateUserAgent();
        var pair = WebGlPresetPairs[Random.Next(WebGlPresetPairs.Length)];
        WebGlVendor = pair.Vendor;
        WebGlRenderer = pair.Renderer;
        SpoofWebGl = true;
        CanvasFingerprintNoise = true;
        AudioFingerprintNoise = true;
        _fpOverview.RegenerateCosmeticHardwareAndSeeds();
        _fpOverview.DoNotTrackMode = "Включить";
        _fpOverview.ScreenMode = "На основе User-Agent";
        _fpOverview.ScreenFollowsUa = true;
        RefreshFingerprintOverview();
    }

    private void ApplySelectedWebGlPreset()
    {
        if (!WebGlPresets.TryGetValue(SelectedWebGlPreset, out var preset))
        {
            return;
        }

        WebGlVendor = preset.Vendor;
        if (preset.Renderers.Length > 0)
        {
            WebGlRenderer = preset.Renderers[Random.Next(preset.Renderers.Length)];
        }
    }

    private void SelectWebGlPresetFromCurrentValues()
    {
        var v = WebGlVendor?.Trim() ?? "";
        var r = WebGlRenderer ?? "";

        if (v.Equals("Google Inc. (Intel Inc.)", StringComparison.Ordinal))
        {
            SelectedWebGlPreset = "Google Inc. (Intel Inc.)";
            return;
        }

        if (v.Equals("Google Inc. (Apple)", StringComparison.Ordinal))
        {
            SelectedWebGlPreset = "Google Inc. (Apple)";
            return;
        }

        if (v.Equals("Apple Inc.", StringComparison.Ordinal))
        {
            SelectedWebGlPreset = "Apple Inc.";
            return;
        }

        if (v.Equals("ARM", StringComparison.Ordinal))
        {
            SelectedWebGlPreset = "ARM";
            return;
        }

        if (v.Equals("Qualcomm", StringComparison.Ordinal))
        {
            SelectedWebGlPreset = "Qualcomm";
            return;
        }

        if (v.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            SelectedWebGlPreset = "Google Inc. (NVIDIA)";
            return;
        }

        if (v.Contains("AMD", StringComparison.OrdinalIgnoreCase))
        {
            SelectedWebGlPreset = r.Contains("7900", StringComparison.Ordinal)
                || r.Contains("7800", StringComparison.Ordinal)
                || r.Contains("7600", StringComparison.Ordinal)
                || r.Contains("5700", StringComparison.Ordinal)
                ? "Google Inc. (AMD) — Windows"
                : "Google Inc. (AMD)";
            return;
        }

        if (v.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            SelectedWebGlPreset = "Google Inc. (Intel)";
            return;
        }

        SelectedWebGlPreset = "Others";
    }

    private void SyncFingerprintOverviewWithLegacyState()
    {
        var raw = _account.FingerprintOverviewJson ?? "{}";
        if (!raw.Contains("doNotTrackMode", StringComparison.OrdinalIgnoreCase))
        {
            _fpOverview.DoNotTrackMode = _account.DoNotTrack ? "Включить" : "Выключить";
        }

        if (!raw.Contains("screenMode", StringComparison.OrdinalIgnoreCase))
        {
            _fpOverview.ScreenMode = _fpOverview.ScreenFollowsUa ? "На основе User-Agent" : "Настроить";
        }

        if (!raw.Contains("webRtcMode", StringComparison.OrdinalIgnoreCase))
        {
            _fpOverview.WebRtcMode = string.IsNullOrWhiteSpace(_account.WebRtcLaunchFlags) ? "Реальный" : "Подмена";
        }
    }

    public string UaPresetHeaderText
    {
        get
        {
            if (UaPresetRows.Count == 0)
            {
                return "Все";
            }

            var allRow = UaPresetRows[0];
            var uaRows = UaPresetRows.Skip(1).ToList();
            var picked = uaRows.Where(static x => x.IsChecked).OrderByDescending(static x => x.UaMajor).ToList();
            if (allRow.IsChecked || picked.Count == uaRows.Count)
            {
                return "Все";
            }

            if (picked.Count == 0)
            {
                return "Все";
            }

            if (picked.Count == 1)
            {
                return picked[0].Label;
            }

            return $"{picked[0].Label} (+{picked.Count - 1})";
        }
    }

    private static string ResolveInitialBrowserVersion(AvitoAccount account)
    {
        return BrowserVersionProvider.GetCurrentChromiumMajorString();
    }

    private void NormalizeBrowserVersionForCurrentBrowser()
    {
        BrowserName = BrowserVersionProvider.CurrentBrowserName;
        BrowserVersion = BrowserVersionProvider.GetCurrentChromiumMajorString();
    }

    private static string NormalizeProxyType(string? value) =>
        string.Equals(value?.Trim(), "socks5", StringComparison.OrdinalIgnoreCase) ? "socks5" : "http";

    private void BuildUaPresetRows()
    {
        UaPresetRows.Add(new UaPresetRow { Label = "Все", IsAllOption = true, UaMajor = null });
        for (var v = 147; v >= 88; v--)
        {
            UaPresetRows.Add(new UaPresetRow { Label = $"UA {v}", IsAllOption = false, UaMajor = v });
        }

        foreach (var r in UaPresetRows)
        {
            r.PropertyChanged += OnUaRowPropertyChanged;
        }

        LoadUaSelectionFromAccountField();
        OnPropertyChanged(nameof(UaPresetHeaderText));
    }

    private void OnUaRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UaPresetRow.IsChecked) || _uaSyncBusy || UaPresetRows.Count == 0)
        {
            return;
        }

        if (sender is not UaPresetRow row)
        {
            return;
        }

        _uaSyncBusy = true;
        try
        {
            var allRow = UaPresetRows[0];
            var uaRows = UaPresetRows.Skip(1).ToList();
            if (row.IsAllOption)
            {
                foreach (var u in uaRows)
                {
                    u.IsChecked = row.IsChecked;
                }
            }
            else
            {
                allRow.IsChecked = uaRows.Count > 0 && uaRows.All(static x => x.IsChecked);
            }

            var any = uaRows.Any(static x => x.IsChecked);
            if (!any && !allRow.IsChecked)
            {
                allRow.IsChecked = true;
                foreach (var u in uaRows)
                {
                    u.IsChecked = true;
                }
            }

            PersistUaSelectionToAccountField();
            OnPropertyChanged(nameof(UaPresetHeaderText));
        }
        finally
        {
            _uaSyncBusy = false;
        }
    }

    private void LoadUaSelectionFromAccountField()
    {
        if (UaPresetRows.Count == 0)
        {
            return;
        }

        _uaSyncBusy = true;
        try
        {
            var raw = UserAgentDevice?.Trim() ?? "Все";
            var allRow = UaPresetRows[0];
            var uaRows = UaPresetRows.Skip(1).ToList();
            if (string.IsNullOrWhiteSpace(raw) || raw.Equals("Все", StringComparison.OrdinalIgnoreCase))
            {
                allRow.IsChecked = true;
                foreach (var u in uaRows)
                {
                    u.IsChecked = true;
                }

                return;
            }

            var selected = new HashSet<int>();
            foreach (var part in raw.Split(['|', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var digits = new string(part.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var maj))
                {
                    selected.Add(maj);
                }
            }

            allRow.IsChecked = false;
            foreach (var u in uaRows)
            {
                u.IsChecked = u.UaMajor.HasValue && selected.Contains(u.UaMajor.Value);
            }

            if (uaRows.All(static x => x.IsChecked))
            {
                allRow.IsChecked = true;
            }
            else if (!uaRows.Any(static x => x.IsChecked))
            {
                allRow.IsChecked = true;
                foreach (var u in uaRows)
                {
                    u.IsChecked = true;
                }
            }
        }
        finally
        {
            _uaSyncBusy = false;
        }
    }

    private void PersistUaSelectionToAccountField()
    {
        if (UaPresetRows.Count == 0)
        {
            UserAgentDevice = "Все";
            return;
        }

        var allRow = UaPresetRows[0];
        var uaRows = UaPresetRows.Skip(1).ToList();
        var picked = uaRows.Where(static x => x.IsChecked).OrderByDescending(static x => x.UaMajor).ToList();
        if (allRow.IsChecked || picked.Count == uaRows.Count)
        {
            UserAgentDevice = "Все";
            return;
        }

        UserAgentDevice = string.Join("|", picked.Select(static x => $"UA {x.UaMajor}"));
        if (string.IsNullOrWhiteSpace(UserAgentDevice))
        {
            UserAgentDevice = "Все";
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        _isLoadingAccountSettings = true;
        try
        {
            if (UaPresetRows.Count == 0)
            {
                BuildUaPresetRows();
            }

            if (!_fingerprintOverviewHooked)
            {
                PropertyChanged += OnPropertyChangedForFingerprintOverview;
                _fingerprintOverviewHooked = true;
            }

            _fpOverview = FingerprintOverviewState.Parse(_account.FingerprintOverviewJson);
            SyncFingerprintOverviewWithLegacyState();
            LoadProxyPresetsFromAccount();
            SelectWebGlPresetFromCurrentValues();

            // Не затираем JSON из БД/вставку: раньше при каждом открытии окна подставлялся экспорт из профиля (часто []).
            if (string.IsNullOrWhiteSpace(_account.CookiesJson))
            {
                if (!string.IsNullOrWhiteSpace(_account.BrowserProfilePath))
                {
                    CookiesJson = await profileCookiesService.ReadCurrentProfileCookiesAsJsonAsync(_account, CancellationToken.None);
                }
            }
            else
            {
                CookiesJson = _account.CookiesJson;
            }
            ImportCookiesOnNextStart = _account.ImportCookiesOnNextStart;

            NormalizeBrowserVersionForCurrentBrowser();
            RefreshFingerprintOverview();
        }
        finally
        {
            _isLoadingAccountSettings = false;
        }
    }

    private void LoadProxyPresetsFromAccount()
    {
        ProxyPresets.Clear();
        try
        {
            var list = JsonSerializer.Deserialize<List<SavedProxyPreset>>(_account.ProxyPresetsJson ?? "[]", PresetJsonReadOptions);
            foreach (var p in list ?? [])
            {
                ProxyPresets.Add(ProxyPresetRowViewModel.FromModel(p));
            }
        }
        catch (JsonException)
        {
        }
    }

    [RelayCommand]
    private void MergeCookiesWithSaved()
    {
        CookiesJson = CookieJsonMerger.Merge(_account.CookiesJson ?? "[]", CookiesJson ?? "[]");
        StatusHint = "Cookie объединены с данными, сохранёнными для аккаунта в базе.";
    }

    [RelayCommand(CanExecute = nameof(CanCheckProxy))]
    private async Task CheckProxyAsync()
    {
        StatusHint = "";
        ProxyCheckResultText = "";
        ProxyCheckIsError = false;
        IsProxyChecking = true;
        try
        {
            var ip = await _proxyCheckService.CheckPublicIpAsync(CreateProxyProbeAccount(), CancellationToken.None);
            ProxyCheckResultText = $"Внешний IP через прокси: {ip}";
            ProxyCheckIsError = false;
            await TryAutoUpdateTimezoneAsync(ip);
        }
        catch (Exception ex)
        {
            ProxyCheckResultText = ex.Message;
            ProxyCheckIsError = true;
        }
        finally
        {
            IsProxyChecking = false;
        }
    }

    private bool CanCheckProxy() => !IsProxyChecking && !string.IsNullOrWhiteSpace(ProxyAddress);

    [RelayCommand]
    private async Task RotateProxyIpAsync()
    {
        StatusHint = "";
        try
        {
            await _proxyCheckService.RequestRotationUrlAsync(ProxyRotationUrl, CancellationToken.None);
            var ip = await _proxyCheckService.CheckPublicIpAsync(CreateProxyProbeAccount(), CancellationToken.None);
            ProxyCheckResultText = $"Внешний IP через прокси: {ip}";
            ProxyCheckIsError = false;
            await TryAutoUpdateTimezoneAsync(ip);
            var successMessage = UseIpTimezone
                ? "Запрос на смену IP отправлен. IP и часовой пояс обновлены."
                : "Запрос на смену IP отправлен. Внешний IP обновлён.";
            MessageBox.Show(
                successMessage,
                "Смена IP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ProxyCheckResultText = ex.Message;
            ProxyCheckIsError = true;
            MessageBox.Show(
                ex.Message,
                "Смена IP",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task TryAutoUpdateTimezoneAsync(string externalIp)
    {
        if (!UseIpTimezone)
        {
            return;
        }

        try
        {
            var timezone = await _proxyCheckService.ResolveTimezoneByIpAsync(externalIp, CancellationToken.None);
            Timezone = timezone;
            StatusHint = $"Часовой пояс автоматически определён: {timezone}.";
        }
        catch (Exception ex)
        {
            StatusHint = $"IP определён, но часовой пояс обновить не удалось: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AddProxyPreset()
    {
        ProxyPresets.Add(new ProxyPresetRowViewModel { Label = "Пресет", ProxyType = NormalizeProxyType(ProxyType) });
    }

    [RelayCommand]
    private void RemoveProxyPreset(ProxyPresetRowViewModel? row)
    {
        if (row is not null)
        {
            ProxyPresets.Remove(row);
        }
    }

    [RelayCommand]
    private void ApplyProxyPreset(ProxyPresetRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        ProxyType = NormalizeProxyType(row.ProxyType);
        ProxyAddress = string.IsNullOrWhiteSpace(row.Address) ? null : row.Address.Trim();
        ProxyUsername = row.Username;
        ProxyPassword = row.Password;
        ProxyRotationUrl = string.IsNullOrWhiteSpace(row.RotationUrl) ? null : row.RotationUrl.Trim();
    }

    private AvitoAccount CreateProxyProbeAccount()
    {
        return new AvitoAccount
        {
            ProxyAddress = string.IsNullOrWhiteSpace(ProxyAddress) ? null : ProxyAddress.Trim(),
            ProxyType = NormalizeProxyType(ProxyType),
            ProxyUsername = string.IsNullOrWhiteSpace(ProxyUsername) ? null : ProxyUsername.Trim(),
            ProxyPassword = ProxyPassword
        };
    }

    private void RecombineProxyAddressFromHostPort()
    {
        var h = ProxyHost?.Trim() ?? "";
        var p = ProxyPort?.Trim() ?? "";
        string? combined = string.IsNullOrEmpty(h) ? null : string.IsNullOrEmpty(p) ? h : $"{h}:{p}";
        var next = string.IsNullOrWhiteSpace(combined) ? null : combined.Trim();
        if (string.Equals(ProxyAddress ?? "", next ?? "", StringComparison.Ordinal))
        {
            CheckProxyCommand.NotifyCanExecuteChanged();
            return;
        }

        _proxyInternalUpdate = true;
        try
        {
            ProxyAddress = next;
        }
        finally
        {
            _proxyInternalUpdate = false;
        }

        CheckProxyCommand.NotifyCanExecuteChanged();
    }

    private static (string Host, string Port) ParseProxyAddressForUi(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return ("", "");
        }

        var a = address.Trim();
        if (Uri.TryCreate(a, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host)
            && (uri.Scheme == Uri.UriSchemeHttp
                || uri.Scheme == Uri.UriSchemeHttps
                || string.Equals(uri.Scheme, "socks5", StringComparison.OrdinalIgnoreCase)))
        {
            var host = uri.Host;
            if (host.Contains(':') && !host.StartsWith('['))
            {
                host = $"[{host}]";
            }

            var port = !uri.IsDefaultPort && uri.Port > 0
                ? uri.Port.ToString(CultureInfo.InvariantCulture)
                : "";
            return (host, port);
        }

        if (a.StartsWith('['))
        {
            var endBracket = a.IndexOf(']', StringComparison.Ordinal);
            if (endBracket > 0 && endBracket + 1 < a.Length && a[endBracket + 1] == ':')
            {
                return (a[..(endBracket + 1)], a[(endBracket + 2)..]);
            }
        }

        var idx = a.LastIndexOf(':');
        if (idx > 0 && idx < a.Length - 1)
        {
            var tail = a[(idx + 1)..];
            if (tail.Length > 0 && tail.All(static c => c is >= '0' and <= '9'))
            {
                return (a[..idx], tail);
            }
        }

        return (a, "");
    }

    /// <summary>
    /// Новая строка Microsoft Edge UA: случайный Chromium-мажор из отмеченных пресетов (или из полного списка пресетов),
    /// независимо от поля «версия» — иначе при режиме «Все» мажор совпадал с установленным Edge и строка не менялась.
    /// </summary>
    public void RegenerateUserAgent()
    {
        NormalizeBrowserVersionForCurrentBrowser();
        var osToken = BuildOsToken();
        var pool = GetChromiumMajorPoolForRegenerateButton();
        var previous = AssignedUserAgent;
        string next;
        var chosenMajor = 0;
        var attempt = 0;
        do
        {
            chosenMajor = pool[Random.Next(pool.Count)];
            next = FormatMicrosoftEdgeUserAgent(osToken, chosenMajor);
            attempt++;
        } while (attempt < 20 && pool.Count > 1 && string.Equals(next, previous, StringComparison.Ordinal));

        AssignedUserAgent = next;
    }

    private List<int> GetChromiumMajorPoolForRegenerateButton()
    {
        if (UaPresetRows.Count <= 1)
        {
            if (int.TryParse(BrowserVersionProvider.GetCurrentChromiumMajorString(), out var wv))
            {
                return [Math.Clamp(wv, 88, 147)];
            }

            if (int.TryParse(BrowserVersion, out var bv))
            {
                return [Math.Clamp(bv, 88, 147)];
            }

            return [131];
        }

        var all = UaPresetRows.Skip(1).Where(static r => r.UaMajor.HasValue).Select(static r => r.UaMajor!.Value).Distinct().ToList();
        var checkedOnly = UaPresetRows.Skip(1).Where(static r => r is { IsChecked: true, UaMajor: not null }).Select(static r => r.UaMajor!.Value).Distinct().ToList();
        var pool = checkedOnly.Count > 0 ? checkedOnly : all;
        if (pool.Count > 0)
        {
            return pool;
        }

        var fallback = int.TryParse(BrowserVersion, out var fb) ? Math.Clamp(fb, 88, 147) : 131;
        return [fallback];
    }

    private static string FormatMicrosoftEdgeUserAgent(string osToken, int major)
    {
        major = Math.Clamp(major, 88, 147);
        return $"Mozilla/5.0 ({osToken}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0";
    }

    private string BuildOsToken()
    {
        if (UseAndroidOs)
        {
            var android = AndroidVersion.Replace("All Android", "Android 14", StringComparison.OrdinalIgnoreCase).Trim();
            return $"Linux; {android}";
        }

        if (UseIosOs)
        {
            var ios = IosVersion.Replace("All iOS", "iOS 17", StringComparison.OrdinalIgnoreCase).Replace("iOS ", string.Empty, StringComparison.OrdinalIgnoreCase);
            return $"iPhone; CPU iPhone OS {ios}_0 like Mac OS X";
        }

        if (UseMacOs)
        {
            return "Macintosh; Intel Mac OS X 10_15_7";
        }

        if (UseLinuxOs)
        {
            return "X11; Linux x86_64";
        }

        var windows = WindowsVersion switch
        {
            "Windows 11" => "Windows NT 10.0; Win64; x64",
            "Windows 8" => "Windows NT 6.2; Win64; x64",
            "Windows 7" => "Windows NT 6.1; Win64; x64",
            _ => "Windows NT 10.0; Win64; x64"
        };

        return windows;
    }

    [RelayCommand]
    public void Save(Window? window)
    {
        PersistUaSelectionToAccountField();
        var name = DisplayName.Trim();
        if (name.Length > MaxDisplayNameLen)
        {
            name = name[..MaxDisplayNameLen];
        }

        var notes = Notes.Trim();
        if (notes.Length > MaxNotesLen)
        {
            notes = notes[..MaxNotesLen];
        }

        _account.DisplayName = name;
        _account.BrowserName = BrowserName;
        _account.BrowserVersion = BrowserVersion;
        _account.UseWindowsOs = UseWindowsOs;
        _account.WindowsVersion = WindowsVersion;
        _account.UseMacOs = UseMacOs;
        _account.MacOsVersion = MacOsVersion;
        _account.UseLinuxOs = UseLinuxOs;
        _account.LinuxVersion = LinuxVersion;
        _account.UseAndroidOs = UseAndroidOs;
        _account.AndroidVersion = AndroidVersion;
        _account.UseIosOs = UseIosOs;
        _account.IosVersion = IosVersion;
        _account.UserAgentDevice = UserAgentDevice;
        _account.AssignedUserAgent = AssignedUserAgent;
        var previousCookiesJson = _account.CookiesJson ?? string.Empty;
        var nextCookiesJson = CookiesJson ?? string.Empty;
        var cookiesJsonChanged = !string.Equals(previousCookiesJson, nextCookiesJson, StringComparison.Ordinal);
        _account.CookiesJson = CookiesJson;
        _account.ImportCookiesOnNextStart = cookiesJsonChanged && !string.IsNullOrWhiteSpace(nextCookiesJson);
        _account.Notes = notes;
        _account.ProxyAddress = string.IsNullOrWhiteSpace(ProxyAddress) ? null : ProxyAddress.Trim();
        _account.ProxyType = NormalizeProxyType(ProxyType);
        _account.ProxyUsername = string.IsNullOrWhiteSpace(ProxyUsername) ? null : ProxyUsername.Trim();
        _account.ProxyPassword = string.IsNullOrWhiteSpace(ProxyPassword) ? null : ProxyPassword;
        _account.ProxyRotationUrl = string.IsNullOrWhiteSpace(ProxyRotationUrl) ? null : ProxyRotationUrl.Trim();
        _account.BrowserLaunchArgs = BrowserLaunchArgs.Trim();
        _account.NavigatorPlatform = string.IsNullOrWhiteSpace(NavigatorPlatform) ? null : NavigatorPlatform.Trim();
        _account.DoNotTrack = DoNotTrackMode == "Включить";
        _account.SpoofWebGl = SpoofWebGl;
        _account.WebGlVendor = string.IsNullOrWhiteSpace(WebGlVendor) ? null : WebGlVendor.Trim();
        _account.WebGlRenderer = string.IsNullOrWhiteSpace(WebGlRenderer) ? null : WebGlRenderer.Trim();
        _account.CanvasFingerprintNoise = CanvasFingerprintNoise;
        _account.AudioFingerprintNoise = AudioFingerprintNoise;
        _account.WebRtcLaunchFlags = string.IsNullOrWhiteSpace(WebRtcLaunchFlags) ? null : WebRtcLaunchFlags.Trim();
        _account.ProxyPresetsJson = JsonSerializer.Serialize(
            ProxyPresets.Select(static x => x.ToModel()).ToList(),
            PresetJsonWriteOptions);
        _account.ScreenResolution = string.IsNullOrWhiteSpace(ScreenResolution) ? "1920x1080" : ScreenResolution.Trim();
        _account.UseIpTimezone = TimezoneMode == "На основе IP";
        _account.Timezone = string.IsNullOrWhiteSpace(Timezone) ? "Europe/Moscow" : Timezone.Trim();
        _account.Languages = string.IsNullOrWhiteSpace(Languages) ? "ru-RU,ru,en-US,en" : Languages.Trim();
        _account.FingerprintOverviewJson = FingerprintOverviewState.Serialize(_fpOverview);

        if (window is null)
        {
            return;
        }

        window.DialogResult = true;
        window.Close();
    }
}

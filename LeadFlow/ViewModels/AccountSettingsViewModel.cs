using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Models;
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
    private bool _uaSyncBusy;
    private bool _fingerprintOverviewHooked;
    private FingerprintOverviewState _fpOverview = FingerprintOverviewState.Parse(account.FingerprintOverviewJson);

    private static readonly (string Vendor, string Renderer)[] WebGlPresetPairs =
    [
        ("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) UHD Graphics 630 Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce GTX 1660 SUPER Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (AMD)", "ANGLE (AMD, AMD Radeon RX 580 Series Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        ("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) Iris(R) Xe Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)")
    ];

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

    public IReadOnlyList<string> BrowserOptions { get; } = ["Chrome", "Edge", "Firefox", "Safari", "Opera"];

    /// <summary>Версия в комбо зависит от браузера: Chromium major, OPR major, Firefox major или Safari Version.</summary>
    public IReadOnlyList<string> BrowserVersionOptions => GetBrowserVersionOptions(BrowserName);

    public IReadOnlyList<string> WindowsVersionOptions { get; } = ["All Windows", "Windows 11", "Windows 10", "Windows 8", "Windows 7"];
    public IReadOnlyList<string> MacOsVersionOptions { get; } = ["All macOS", "macOS 26", "macOS 15", "macOS 14", "macOS 13", "macOS 12", "macOS 11", "macOS 10"];
    public IReadOnlyList<string> LinuxVersionOptions { get; } = ["Linux x86_64", "Ubuntu", "Debian", "Fedora"];
    public IReadOnlyList<string> AndroidVersionOptions { get; } = ["All Android", "Android 15", "Android 14", "Android 13", "Android 12", "Android 11", "Android 10", "Android 9"];
    public IReadOnlyList<string> IosVersionOptions { get; } = ["All iOS", "iOS 18", "iOS 17", "iOS 16", "iOS 15"];

    public IReadOnlyList<string> ProxyTypeOptions { get; } = ["http", "socks5"];

    public ObservableCollection<UaPresetRow> UaPresetRows { get; } = [];

    [ObservableProperty] private bool isUaPresetDropdownOpen;
    [ObservableProperty] private string displayName = account.DisplayName;
    [ObservableProperty] private string browserName = string.IsNullOrWhiteSpace(account.BrowserName) ? "Chrome" : account.BrowserName;
    [ObservableProperty] private string browserVersion = string.IsNullOrWhiteSpace(account.BrowserVersion) ? "146" : account.BrowserVersion;
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
    [ObservableProperty] private string proxyType = NormalizeProxyType(account.ProxyType);
    [ObservableProperty] private string screenResolution = string.IsNullOrWhiteSpace(account.ScreenResolution) ? "1920x1080" : account.ScreenResolution!;
    [ObservableProperty] private string timezone = string.IsNullOrWhiteSpace(account.Timezone) ? "Europe/Moscow" : account.Timezone!;
    [ObservableProperty] private string languages = string.IsNullOrWhiteSpace(account.Languages) ? "ru-RU,ru,en-US,en" : account.Languages!;
    [ObservableProperty] private string? proxyUsername = account.ProxyUsername;
    [ObservableProperty] private string? proxyPassword = account.ProxyPassword;
    [ObservableProperty] private string? proxyRotationUrl = account.ProxyRotationUrl;
    [ObservableProperty] private string browserLaunchArgs = account.BrowserLaunchArgs ?? "";
    [ObservableProperty] private string? navigatorPlatform = account.NavigatorPlatform;
    [ObservableProperty] private bool doNotTrack = account.DoNotTrack;
    [ObservableProperty] private bool spoofWebGl = account.SpoofWebGl;
    [ObservableProperty] private string? webGlVendor = account.WebGlVendor;
    [ObservableProperty] private string? webGlRenderer = account.WebGlRenderer;
    [ObservableProperty] private bool canvasFingerprintNoise = account.CanvasFingerprintNoise;
    [ObservableProperty] private bool audioFingerprintNoise = account.AudioFingerprintNoise;
    [ObservableProperty] private string? webRtcLaunchFlags = account.WebRtcLaunchFlags;
    [ObservableProperty] private string statusHint = "";

    public ObservableCollection<ProxyPresetRowViewModel> ProxyPresets { get; } = [];

    public string DisplayNameCounter => $"{DisplayName.Length} / {MaxDisplayNameLen}";

    public string NotesCounter => $"{Notes.Length} / {MaxNotesLen}";

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(DisplayNameCounter));

    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(NotesCounter));
    partial void OnScreenResolutionChanged(string value) => _fpOverview.ScreenFollowsUa = false;

    public string OverviewLanguageText =>
        string.IsNullOrWhiteSpace(AssignedUserAgent) ? "—" : "На основе языка";

    public string OverviewScreenText =>
        _fpOverview.ScreenFollowsUa && !string.IsNullOrWhiteSpace(AssignedUserAgent)
            ? "На основе User-Agent"
            : (string.IsNullOrWhiteSpace(ScreenResolution) ? "—" : ScreenResolution);

    public string OverviewFontsText => "По умолчанию";

    public string OverviewCanvasText => CanvasFingerprintNoise ? "Шум" : "Реальный";

    public string OverviewWebGlImageText =>
        SpoofWebGl && !string.IsNullOrWhiteSpace(WebGlVendor) ? "Подмена" : "Реальный";

    public string OverviewAudioText => AudioFingerprintNoise
        ? (string.IsNullOrWhiteSpace(_fpOverview.AudioSeedHex) ? "Шум" : $"Шум [{_fpOverview.AudioSeedHex}]")
        : "Реальный";

    public string OverviewMediaDevicesText =>
        string.IsNullOrWhiteSpace(_fpOverview.MediaLabel) ? "—" : $"Шум [{_fpOverview.MediaLabel}]";

    public string OverviewClientRectsText =>
        string.IsNullOrWhiteSpace(_fpOverview.ClientRectsSeedHex)
            ? "—"
            : $"Шум [{_fpOverview.ClientRectsSeedHex}]";

    public string OverviewSpeechVoicesText =>
        string.IsNullOrWhiteSpace(_fpOverview.SpeechLabel) ? "—" : $"Шум [{_fpOverview.SpeechLabel}]";

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

    public string OverviewWebGpuText => "По WebGL (ограничено в WebView2)";

    public string OverviewCpuText =>
        _fpOverview.HardwareConcurrency > 0 ? $"{_fpOverview.HardwareConcurrency} ядер" : "—";

    public string OverviewRamText =>
        _fpOverview.DeviceMemoryGb > 0 ? $"{_fpOverview.DeviceMemoryGb} GB" : "—";

    public string OverviewDeviceNameText =>
        string.IsNullOrWhiteSpace(_fpOverview.DeviceName) ? "—" : _fpOverview.DeviceName;

    public string OverviewMacText =>
        string.IsNullOrWhiteSpace(_fpOverview.MacAddress) ? "—" : _fpOverview.MacAddress;

    public string OverviewDntText => DoNotTrack ? "Включён (1)" : "Отключён (null)";

    public string OverviewWebRtcText =>
        string.IsNullOrWhiteSpace(WebRtcLaunchFlags) ? "По умолчанию" : "Задано в поле";

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
        RefreshFingerprintOverview();
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

    partial void OnBrowserNameChanged(string value)
    {
        OnPropertyChanged(nameof(BrowserVersionOptions));
        NormalizeBrowserVersionForCurrentBrowser();
    }

    private static IReadOnlyList<string> GetBrowserVersionOptions(string? browserName)
    {
        return (browserName ?? "Chrome").Trim() switch
        {
            "Firefox" => BuildVersionStringsDescending(140, 88),
            "Safari" => ["18", "17", "16", "15"],
            "Opera" => BuildVersionStringsDescending(120, 70),
            _ => BuildVersionStringsDescending(147, 88)
        };
    }

    private static List<string> BuildVersionStringsDescending(int high, int low)
    {
        var list = new List<string>();
        for (var v = high; v >= low; v--)
        {
            list.Add(v.ToString());
        }

        return list;
    }

    private void NormalizeBrowserVersionForCurrentBrowser()
    {
        var opts = BrowserVersionOptions;
        if (opts.Count == 0)
        {
            return;
        }

        if (!opts.Contains(BrowserVersion))
        {
            BrowserVersion = opts[0];
        }
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
        LoadProxyPresetsFromAccount();

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

    [RelayCommand]
    private async Task CheckProxyAsync()
    {
        StatusHint = "";
        try
        {
            var ip = await _proxyCheckService.CheckPublicIpAsync(CreateProxyProbeAccount(), CancellationToken.None);
            MessageBox.Show(
                $"Исходящий IP через прокси: {ip}",
                "Проверка прокси",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Проверка прокси",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task RotateProxyIpAsync()
    {
        StatusHint = "";
        try
        {
            await _proxyCheckService.RequestRotationUrlAsync(ProxyRotationUrl, CancellationToken.None);
            MessageBox.Show(
                "Запрос на смену IP отправлен.",
                "Смена IP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Смена IP",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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

    /// <summary>
    /// Случайный браузер + случайная допустимая для него версия в комбо, затем UA строго под эти поля
    /// (раньше всегда использовался один и тот же выбранный браузер).
    /// </summary>
    public void RegenerateUserAgent()
    {
        var previous = AssignedUserAgent;
        string next;
        var attempt = 0;
        do
        {
            var browserPick = BrowserOptions[Random.Next(BrowserOptions.Count)];
            BrowserName = browserPick;

            var verOpts = GetBrowserVersionOptions(browserPick);
            if (verOpts.Count > 0)
            {
                BrowserVersion = verOpts[Random.Next(verOpts.Count)];
            }

            var osToken = BuildOsToken();
            var b = browserPick.Trim();
            next = b switch
            {
                "Edge" => BuildChromiumEdgeUserAgent(osToken),
                "Firefox" => BuildFirefoxUserAgent(osToken),
                "Safari" => BuildSafariUserAgent(osToken),
                "Opera" => BuildOperaUserAgent(osToken),
                _ => BuildChromeUserAgent(osToken)
            };
            attempt++;
        } while (attempt < 24 && string.Equals(next, previous, StringComparison.Ordinal));

        AssignedUserAgent = next;
    }

    private string BuildChromeUserAgent(string osToken, bool preferRandomAmongAllowedMajors = false)
    {
        var maj = ResolveChromiumMajorForChromeFamily(preferRandomAmongAllowedMajors);
        return $"Mozilla/5.0 ({osToken}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{maj}.0.0.0 Safari/537.36";
    }

    private string BuildChromiumEdgeUserAgent(string osToken, bool preferRandomAmongAllowedMajors = false)
    {
        var maj = ResolveChromiumMajorForChromeFamily(preferRandomAmongAllowedMajors);
        return $"Mozilla/5.0 ({osToken}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{maj}.0.0.0 Safari/537.36 Edg/{maj}.0.0.0";
    }

    /// <summary>
    /// Opera: в комбо — мажор OPR (как у реального Opera). В строке UA — OPR/{opr} и отдельно Chrome/{chrome},
    /// где chrome ≈ opr + 16 (типичная связка Chromium-ядра и OPR), чтобы не слать сайту «Opera 146».
    /// </summary>
    private string BuildOperaUserAgent(string osToken, bool preferRandomVersion = false)
    {
        int opr;
        if (preferRandomVersion)
        {
            opr = Random.Next(70, 131);
        }
        else
        {
            opr = int.TryParse(BrowserVersion, out var o) ? o : 115;
            opr = Math.Clamp(opr, 70, 130);
        }

        var chrome = Math.Clamp(opr + 16, 88, 147);
        return $"Mozilla/5.0 ({osToken}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chrome}.0.0.0 Safari/537.36 OPR/{opr}.0.0.0";
    }

    private string BuildFirefoxUserAgent(string osToken, bool preferRandomVersion = false)
    {
        int ff;
        if (preferRandomVersion)
        {
            ff = Random.Next(88, 141);
        }
        else
        {
            ff = int.TryParse(BrowserVersion, out var v) ? v : 128;
            ff = Math.Clamp(ff, 88, 200);
        }

        return $"Mozilla/5.0 ({osToken}; rv:{ff}.0) Gecko/20100101 Firefox/{ff}.0";
    }

    private string BuildSafariUserAgent(string osToken, bool preferRandomVersion = false)
    {
        string ver;
        if (preferRandomVersion)
        {
            var opts = GetBrowserVersionOptions("Safari");
            ver = opts[Random.Next(opts.Count)];
        }
        else
        {
            ver = string.IsNullOrWhiteSpace(BrowserVersion) ? "18" : BrowserVersion.Trim();
        }

        if (!ver.Contains('.', StringComparison.Ordinal))
        {
            ver = $"{ver}.0";
        }

        return $"Mozilla/5.0 ({osToken}) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/{ver} Safari/605.1.15";
    }

    /// <summary>Chromium major для Chrome/Edge: из пресетов UA или из поля версии браузера.</summary>
    /// <param name="preferRandomAmongAllowedMajors">Для кнопки перегенерации: случайный мажор среди отмеченных пресетов (или всех 88–147), без «залипания» на версии из комбо.</param>
    private int ResolveChromiumMajorForChromeFamily(bool preferRandomAmongAllowedMajors = false)
    {
        if (UaPresetRows.Count == 0)
        {
            return int.TryParse(BrowserVersion, out var b) ? Math.Clamp(b, 88, 147) : 146;
        }

        var allMajorsFromPresets = UaPresetRows.Skip(1).Select(static x => x.UaMajor!.Value).ToList();
        var checkedMajors = UaPresetRows.Skip(1).Where(static x => x.IsChecked).Select(static x => x.UaMajor!.Value).ToList();

        if (preferRandomAmongAllowedMajors)
        {
            var pool = checkedMajors.Count > 0 ? checkedMajors : allMajorsFromPresets;
            return pool.Count > 0 ? pool[Random.Next(pool.Count)] : (int.TryParse(BrowserVersion, out var b) ? Math.Clamp(b, 88, 147) : 146);
        }

        var allRow = UaPresetRows[0];
        var uaRows = checkedMajors;
        if (allRow.IsChecked || uaRows.Count == 0 || uaRows.Count == UaPresetRows.Count - 1)
        {
            if (int.TryParse(BrowserVersion, out var fromCombo))
            {
                return Math.Clamp(fromCombo, 88, 147);
            }

            return uaRows.Count > 0 ? uaRows[Random.Next(uaRows.Count)] : 146;
        }

        return uaRows[Random.Next(uaRows.Count)];
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
        _account.DoNotTrack = DoNotTrack;
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

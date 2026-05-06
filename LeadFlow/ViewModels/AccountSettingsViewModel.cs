using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Models;
using LeadFlow.Services.Browser;

namespace LeadFlow.ViewModels;

public partial class AccountSettingsViewModel(
    AvitoAccount account,
    IProfileCookiesService profileCookiesService) : ObservableObject
{
    private static readonly Random Random = new();
    private readonly AvitoAccount _account = account;
    private bool _uaSyncBusy;

    public IReadOnlyList<string> BrowserOptions { get; } = ["Chrome", "Edge", "Firefox", "Safari", "Opera"];

    /// <summary>Версия в комбо зависит от браузера: Chromium major, OPR major, Firefox major или Safari Version.</summary>
    public IReadOnlyList<string> BrowserVersionOptions => GetBrowserVersionOptions(BrowserName);

    public IReadOnlyList<string> WindowsVersionOptions { get; } = ["All Windows", "Windows 11", "Windows 10", "Windows 8", "Windows 7"];
    public IReadOnlyList<string> MacOsVersionOptions { get; } = ["All macOS", "macOS 26", "macOS 15", "macOS 14", "macOS 13", "macOS 12", "macOS 11", "macOS 10"];
    public IReadOnlyList<string> LinuxVersionOptions { get; } = ["Linux x86_64", "Ubuntu", "Debian", "Fedora"];
    public IReadOnlyList<string> AndroidVersionOptions { get; } = ["All Android", "Android 15", "Android 14", "Android 13", "Android 12", "Android 11", "Android 10", "Android 9"];
    public IReadOnlyList<string> IosVersionOptions { get; } = ["All iOS", "iOS 18", "iOS 17", "iOS 16", "iOS 15"];

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
    [ObservableProperty] private string notes = account.Notes;

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

        NormalizeBrowserVersionForCurrentBrowser();
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
        _account.DisplayName = DisplayName;
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
        _account.CookiesJson = CookiesJson;
        _account.Notes = Notes;

        if (window is null)
        {
            return;
        }

        window.DialogResult = true;
        window.Close();
    }
}

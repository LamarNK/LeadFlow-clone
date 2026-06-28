using System.Globalization;
using System.Windows.Forms;

using Microsoft.Win32;

namespace LeadFlow.Services.AntiDetect;

/// <summary>
/// Параметры отпечатка, совпадающие с текущим ПК (без рандомизации «антидетекта»).
/// </summary>
public static class HostFingerprintProvider
{
    public static string GetPrimaryScreenResolution()
    {
        try
        {
            var screen = Screen.PrimaryScreen;
            if (screen is null)
            {
                return "1920x1080";
            }

            var b = screen.Bounds;
            var w = Math.Max(800, b.Width);
            var h = Math.Max(600, b.Height);
            return $"{w}x{h}";
        }
        catch
        {
            return "1920x1080";
        }
    }

    /// <summary>IANA id, если доступно преобразование с Windows.</summary>
    public static string GetLocalIanaTimeZoneId()
    {
        try
        {
            var local = TimeZoneInfo.Local;
            if (OperatingSystem.IsWindows()
                && TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana)
                && !string.IsNullOrWhiteSpace(iana))
            {
                return iana;
            }

            return string.IsNullOrWhiteSpace(local.Id) ? "UTC" : local.Id;
        }
        catch
        {
            return "UTC";
        }
    }

    /// <summary>Список языков в стиле Accept-Language для поля аккаунта.</summary>
    public static string GetAcceptLanguageStyleList()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return;
            }

            set.Add(s.Trim());
        }

        Add(CultureInfo.CurrentUICulture.Name);
        Add(CultureInfo.CurrentCulture.Name);
        var p = CultureInfo.CurrentUICulture;
        while (!string.IsNullOrEmpty(p.Parent.Name))
        {
            Add(p.Parent.Name);
            p = p.Parent;
        }

        if (!set.Any(static x => x.StartsWith("en", StringComparison.OrdinalIgnoreCase)))
        {
            Add("en-US");
            Add("en");
        }

        return string.Join(",", set);
    }

    /// <summary>Метка как в UI аккаунта (<see cref="ClientHintsSpoof"/>).</summary>
    public static string GetWindowsVersionUiLabel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Windows 10";
        }

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var buildStr = k?.GetValue("CurrentBuild") as string;
            if (int.TryParse(buildStr, out var build) && build >= 22000)
            {
                return "Windows 11";
            }
        }
        catch
        {
        }

        return "Windows 10";
    }

    public static void ApplyHostOsFlagsToAccount(AvitoAccount account)
    {
        if (OperatingSystem.IsWindows())
        {
            account.UseWindowsOs = true;
            account.UseMacOs = false;
            account.UseLinuxOs = false;
            account.UseAndroidOs = false;
            account.UseIosOs = false;
            account.WindowsVersion = GetWindowsVersionUiLabel();
            return;
        }

        account.UseWindowsOs = false;
        if (OperatingSystem.IsMacOS())
        {
            account.UseMacOs = true;
            account.UseLinuxOs = false;
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            account.UseMacOs = false;
            account.UseLinuxOs = true;
        }
    }
}

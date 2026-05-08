using LeadFlow.Models;

namespace LeadFlow.Services.AntiDetect;

/// <summary>
/// Значения Client Hints (Sec-CH-UA-Platform / Platform-Version), согласованные с выбором ОС в аккаунте.
/// Классический User-Agent для Win10 и Win11 часто одинаковый (Windows NT 10.0); сайты отличают ОС по CH.
/// </summary>
public static class ClientHintsSpoof
{
    /// <param name="Mobile"><c>Sec-CH-UA-Mobile</c>: <c>?0</c> десктоп, <c>?1</c> мобильный профиль.</param>
    public sealed record Values(string? Platform, string? PlatformVersion, string Mobile);

    /// <summary>Те же приоритеты ОС, что и при сборке токена UA в настройках аккаунта.</summary>
    public static Values GetForAccount(AvitoAccount account)
    {
        if (account.UseAndroidOs)
        {
            return new Values("Android", "14.0.0", "?1");
        }

        if (account.UseIosOs)
        {
            return new Values("iOS", "17.0.0", "?1");
        }

        if (account.UseMacOs)
        {
            return new Values("macOS", "14.0.0", "?0");
        }

        if (account.UseLinuxOs)
        {
            return new Values("Linux", "5.15.0", "?0");
        }

        var v = account.WindowsVersion?.Trim() switch
        {
            "Windows 11" => "15.0.0",
            "Windows 8" => "6.2.0",
            "Windows 7" => "6.1.0",
            _ => "10.0.0"
        };

        return new Values("Windows", v, "?0");
    }
}

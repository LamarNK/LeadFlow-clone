using System.Text.Json;

namespace LeadFlow.Models;

/// <summary>
/// Доп. параметры отпечатка для панели «Обзор» и стабильных seed в stealth-скрипте.
/// </summary>
public sealed class FingerprintOverviewState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public string AudioSeedHex { get; set; } = "";

    public string ClientRectsSeedHex { get; set; } = "";

    public string MediaLabel { get; set; } = "Auto";

    public string SpeechLabel { get; set; } = "Auto";

    public string LanguageMode { get; set; } = "На основе IP";

    public string UiLanguageMode { get; set; } = "Реальный";

    public string CustomUiLanguage { get; set; } = "";

    public string ScreenMode { get; set; } = "На основе User-Agent";

    public string FontsMode { get; set; } = "По умолчанию";

    public string CustomFonts { get; set; } = "";

    public string GeolocationMode { get; set; } = "На основе IP";

    public string GeolocationLatitude { get; set; } = "";

    public string GeolocationLongitude { get; set; } = "";

    public string GeolocationAccuracyMeters { get; set; } = "";

    public string WebRtcMode { get; set; } = "Реальный";

    public string MediaDevicesMode { get; set; } = "Реальный";

    public string ClientRectsMode { get; set; } = "Реальный";

    public string SpeechVoicesMode { get; set; } = "Реальный";

    public string WebGpuMode { get; set; } = "Реальный";

    public string CpuMode { get; set; } = "Реальный";

    public string RamMode { get; set; } = "Реальный";

    public string DeviceNameMode { get; set; } = "Реальный";

    public string MacAddressMode { get; set; } = "Реальный";

    public string DoNotTrackMode { get; set; } = "По умолчанию";

    public bool PortScanProtectionEnabled { get; set; }

    public string AllowedPortScanPorts { get; set; } = "";

    public string HardwareAccelerationMode { get; set; } = "По умолчанию";

    public bool DisableTlsFeatures { get; set; }

    public int DeviceMemoryGb { get; set; } = 8;

    public int HardwareConcurrency { get; set; } = 8;

    public string DeviceName { get; set; } = "";

    public string MacAddress { get; set; } = "";

    /// <summary>Показывать в обзоре формулировку «как у UA» для разрешения.</summary>
    public bool ScreenFollowsUa { get; set; }

    public static FingerprintOverviewState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
        {
            return new FingerprintOverviewState();
        }

        try
        {
            return JsonSerializer.Deserialize<FingerprintOverviewState>(json, JsonOptions) ?? new FingerprintOverviewState();
        }
        catch (JsonException)
        {
            return new FingerprintOverviewState();
        }
    }

    public static string Serialize(FingerprintOverviewState state) =>
        JsonSerializer.Serialize(state, JsonOptions);

    public void RegenerateCosmeticHardwareAndSeeds()
    {
        AudioSeedHex = NewHex(8);
        ClientRectsSeedHex = NewHex(8);
        MediaLabel = "Auto";
        SpeechLabel = "Auto";
        DeviceName = NewDeviceTag();
        MacAddress = NewMac();
        GeolocationAccuracyMeters = "25";
        HardwareConcurrency = Random.Shared.Next(4, 17);
        DeviceMemoryGb = Random.Shared.Next(0, 6) switch
        {
            0 or 1 => 4,
            2 or 3 => 8,
            4 => 16,
            _ => 32
        };
        ScreenFollowsUa = true;
    }

    private static string NewHex(int bytes)
    {
        Span<byte> b = stackalloc byte[bytes];
        Random.Shared.NextBytes(b);
        return Convert.ToHexString(b);
    }

    private static string NewDeviceTag()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, 10).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }

    private static string NewMac()
    {
        Span<byte> b = stackalloc byte[6];
        Random.Shared.NextBytes(b);
        b[0] = (byte)((b[0] | 0x02) & 0xFE);
        return string.Join("-", b.ToArray().Select(x => x.ToString("X2")));
    }
}

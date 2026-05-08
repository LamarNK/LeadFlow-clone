using System.Text.Json.Serialization;

namespace LeadFlow.Models;

/// <summary>
/// Набор параметров цифрового отпечатка (фингерпринта) для аккаунта.
/// Используется для консистентной идентификации браузера при каждом запуске.
/// </summary>
public sealed class AccountFingerprint
{
    /// <summary>
    /// User-Agent строка браузера
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Разрешение экрана в формате "ШиринaxВысота"
    /// </summary>
    public string? ScreenResolution { get; set; }

    /// <summary>
    /// Часовой пояс (IANA timezone, напр. "Europe/Moscow")
    /// </summary>
    public string? Timezone { get; set; }

    /// <summary>
    /// Языки браузера через запятую
    /// </summary>
    public string? Languages { get; set; }

    /// <summary>
    /// Ширина viewport в пикселях
    /// </summary>
    public int ViewportWidth { get; set; } = 1920;

    /// <summary>
    /// Высота viewport в пикселях
    /// </summary>
    public int ViewportHeight { get; set; } = 1080;

    /// <summary>
    /// Глубина цвета (обычно 24)
    /// </summary>
    public int ColorDepth { get; set; } = 24;

    /// <summary>
    /// Объём памяти устройства в ГБ (4, 8, 16, 32)
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DeviceMemory { get; set; } = 8;

    /// <summary>
    /// Количество логических процессоров
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HardwareConcurrency { get; set; } = 8;

    /// <summary><see cref="Navigator.platform"/></summary>
    public string? NavigatorPlatform { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DoNotTrack { get; set; }

    public bool SpoofWebGl { get; set; }

    public string? WebGlVendor { get; set; }

    public string? WebGlRenderer { get; set; }

    public bool CanvasNoise { get; set; } = true;

    public bool AudioNoise { get; set; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AudioNoiseSeedHex { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientRectsNoiseSeedHex { get; set; }

    /// <summary>Низкоэнтропийный <c>navigator.userAgentData.platform</c> (например Windows, macOS).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChPlatform { get; set; }

    /// <summary>Версия платформы для CH / getHighEntropyValues (например 10.0.0 vs 15.0.0 для Win10 и Win11).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChPlatformVersion { get; set; }

    /// <summary>Низкоэнтропийный Client Hint mobile: <c>?0</c> / <c>?1</c> для согласования с <see cref="ChPlatform"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChMobile { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UiLanguage { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeolocationMode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeolocationLatitude { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeolocationLongitude { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeolocationAccuracyMeters { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaDevicesMode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaLabel { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ClientRectsNoise { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpeechVoicesMode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpeechLabel { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebGpuMode { get; set; }

    /// <summary>
    /// Применяет параметры фингерпринта к аккаунту
    /// </summary>
    public void ApplyToAccount(AvitoAccount account)
    {
        account.AssignedUserAgent = UserAgent;
        account.ScreenResolution = ScreenResolution;
        account.Timezone = Timezone;
        account.Languages = Languages;
        // BrowserProfilePath и прокси настраиваются отдельно
    }
}

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
    public int DeviceMemory { get; set; } = 8;

    /// <summary>
    /// Количество логических процессоров
    /// </summary>
    public int HardwareConcurrency { get; set; } = 8;

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

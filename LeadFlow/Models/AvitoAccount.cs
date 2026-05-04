namespace LeadFlow.Models;

public sealed class AvitoAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string AvitoResponsesUrl { get; set; } = "https://www.avito.ru";
    public string BrowserProfilePath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public AvitoAccountStatus Status { get; set; } = AvitoAccountStatus.NotConfigured;
    public DateTime? LastAuthCheckAt { get; set; }
    public DateTime? LastMonitoringAt { get; set; }
    public string LastErrorMessage { get; set; } = string.Empty;

    // === АНТИ-ДЕТЕКТ: Фингерпринт аккаунта (генерируется один раз при создании) ===
    
    /// <summary>
    /// Фиксированный User-Agent для этого аккаунта. Должен соответствовать версии WebView2.
    /// </summary>
    public string? AssignedUserAgent { get; set; }

    /// <summary>
    /// Разрешение экрана в формате "ШиринaxВысота" (напр. "1920x1080")
    /// </summary>
    public string? ScreenResolution { get; set; } = "1920x1080";

    /// <summary>
    /// Часовой пояс (напр. "Europe/Moscow")
    /// </summary>
    public string? Timezone { get; set; } = "Europe/Moscow";

    /// <summary>
    /// Языки браузера через запятую (напр. "ru-RU,ru,en-US,en")
    /// </summary>
    public string? Languages { get; set; } = "ru-RU,ru,en-US,en";

    /// <summary>
    /// Прокси-сервер в формате "user:pass@ip:port" или null если не используется
    /// </summary>
    public string? ProxyAddress { get; set; }

    /// <summary>
    /// Тип прокси: "http" или "socks5"
    /// </summary>
    public string ProxyType { get; set; } = "http";

    // === Статистика объявлений (парсится с профиля Авито) ===
    
    /// <summary>
    /// Количество активных объявлений на Авито (парсится с /profile/pro/items)
    /// </summary>
    public int ActiveAdsCount { get; set; }

    /// <summary>
    /// Количество объявлений с ошибками/заблокированных на Авито (парсится с /profile/pro/items)
    /// </summary>
    public int BlockedCount { get; set; }

    /// <summary>
    /// Количество черновиков на Авито (парсится с /profile/pro/items)
    /// </summary>
    public int DraftsCount { get; set; }

    /// <summary>
    /// Время последнего обновления статистики объявлений (UTC)
    /// </summary>
    public DateTime? AdsStatsUpdatedAt { get; set; }
}

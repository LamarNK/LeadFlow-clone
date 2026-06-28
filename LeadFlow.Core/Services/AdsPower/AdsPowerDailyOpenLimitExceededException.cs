namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Local API AdsPower вернул ошибку дневного лимита запусков браузера (<c>browser/start</c>).
/// Используется для фильтрации в логах (<see cref="ErrorKey"/>) и чтобы мониторинг не дёргал остальные суб-профили.
/// </summary>
public sealed class AdsPowerDailyOpenLimitExceededException : InvalidOperationException
{
    public const string ErrorKey = "ads_power.daily_open_limit";

    public AdsPowerDailyOpenLimitExceededException(int apiCode, string? apiMessage)
        : base(FormatMessage(apiCode, apiMessage))
    {
        ApiCode = apiCode;
        ApiMessage = apiMessage;
    }

    public int ApiCode { get; }

    public string? ApiMessage { get; }

    /// <summary>Эвристика по коду/тексту ответа AdsPower (формулировки могут меняться между версиями).</summary>
    public static bool LooksLikeDailyOpenLimit(int apiCode, string? apiMessage)
    {
        if (apiCode != -1 || string.IsNullOrWhiteSpace(apiMessage))
        {
            return false;
        }

        var m = apiMessage;
        return m.Contains("daily limit", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("open daily", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("Exceeding open", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatMessage(int apiCode, string? apiMessage) =>
        $"AdsPower browser/start: {apiMessage ?? "превышен дневной лимит запусков"} (code {apiCode})";
}

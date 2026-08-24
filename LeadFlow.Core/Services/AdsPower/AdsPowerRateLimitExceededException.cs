namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Local API AdsPower вернул ошибку частоты запросов (<c>Too many request per second</c>).
/// После исчерпания повторов в <see cref="AdsPowerApiClient"/> — чтобы мониторинг не останавливал весь цикл.
/// </summary>
public sealed class AdsPowerRateLimitExceededException : InvalidOperationException
{
    public const string ErrorKey = "ads_power.rate_limit";

    public AdsPowerRateLimitExceededException(int apiCode, string? apiMessage)
        : base(FormatMessage(apiCode, apiMessage))
    {
        ApiCode = apiCode;
        ApiMessage = AdsPowerStartupLogSanitizer.LimitText(apiMessage);
    }

    public int ApiCode { get; }

    /// <summary>Санитизированный текст AdsPower для логов/UI. Сырой msg используется только при классификации до throw.</summary>
    public string? ApiMessage { get; }

    private static string FormatMessage(int apiCode, string? apiMessage)
    {
        var safe = AdsPowerStartupLogSanitizer.LimitText(apiMessage);
        return $"AdsPower: {(string.IsNullOrEmpty(safe) ? "слишком много запросов в секунду" : safe)} (code {apiCode})";
    }
}

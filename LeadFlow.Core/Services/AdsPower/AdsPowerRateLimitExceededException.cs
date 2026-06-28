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
        ApiMessage = apiMessage;
    }

    public int ApiCode { get; }

    public string? ApiMessage { get; }

    private static string FormatMessage(int apiCode, string? apiMessage) =>
        $"AdsPower: {apiMessage ?? "слишком много запросов в секунду"} (code {apiCode})";
}
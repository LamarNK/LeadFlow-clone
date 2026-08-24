using Orbita.Contracts;

namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Профиль AdsPower уже открыт другим пользователем/сессией — повторный browser/start запрещён.
/// </summary>
public sealed class AdsPowerProfileInUseException : InvalidOperationException
{
    public const string ErrorKey = "ads_power.profile_in_use";

    public AdsPowerProfileInUseException(int apiCode, string? apiMessage)
        : base(FormatMessage(apiCode, apiMessage))
    {
        ApiCode = apiCode;
        ApiMessage = AdsPowerStartupLogSanitizer.LimitText(apiMessage);
        UserMessage = AdsPowerStartupLogSanitizer.LimitText(
            AdsPowerErrorMessageNormalizer.NormalizeForDisplay(apiMessage ?? string.Empty));
    }

    public int ApiCode { get; }

    /// <summary>Санитизированный текст AdsPower для логов/UI. Сырой msg используется только в <see cref="LooksLikeProfileInUse"/>.</summary>
    public string? ApiMessage { get; }

    public string UserMessage { get; }

    public static bool LooksLikeProfileInUse(int apiCode, string? apiMessage) =>
        apiCode == -1 && AdsPowerErrorMessageNormalizer.LooksLikeProfileInUse(apiMessage);

    private static string FormatMessage(int apiCode, string? apiMessage)
    {
        var safe = AdsPowerStartupLogSanitizer.LimitText(apiMessage);
        return $"AdsPower: {(string.IsNullOrEmpty(safe) ? "профиль уже используется" : safe)} (code {apiCode})";
    }
}

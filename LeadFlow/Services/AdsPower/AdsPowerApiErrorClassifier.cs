namespace LeadFlow.Services.AdsPower;

internal static class AdsPowerApiErrorClassifier
{
    public static bool LooksLikeRateLimit(int apiCode, string? apiMessage) =>
        apiCode == -1 && LooksLikeRateLimitMessage(apiMessage);

    public static bool LooksLikeRateLimitMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("too many request", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("per second", StringComparison.OrdinalIgnoreCase);
    }
}
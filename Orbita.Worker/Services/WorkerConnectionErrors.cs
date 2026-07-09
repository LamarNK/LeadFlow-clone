namespace Orbita.Worker.Services;

internal static class WorkerConnectionErrors
{
    public const string PausedStatus = "Приостановлен";
    public const string PausedDetail = "Включите воркер в панели Орбиты";
    public const string ErrorStatus = "Ошибка";

    public static bool TryApplyUnauthorized(WorkerRuntimeState runtimeState)
    {
        runtimeState.Status = PausedStatus;
        runtimeState.Detail = PausedDetail;
        return true;
    }

    public static void ApplyHubFailure(WorkerRuntimeState runtimeState, Exception ex)
    {
        if (IsUnauthorized(ex))
        {
            TryApplyUnauthorized(runtimeState);
            return;
        }

        runtimeState.Status = ErrorStatus;
        runtimeState.Detail = ShortenHubMessage(ex.Message);
    }

    public static string? FormatConfigFailure(int statusCode) =>
        statusCode == 401 ? PausedDetail : null;

    public static bool IsUnauthorizedFailure(int statusCode) => statusCode == 401;

    private static bool IsUnauthorized(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (IsUnauthorizedMessage(current.Message))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnauthorizedMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("401", StringComparison.Ordinal)
            || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortenHubMessage(string message)
    {
        const string noisyPrefix = "Response status code does not indicate success: ";
        if (message.StartsWith(noisyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return message[noisyPrefix.Length..].Trim();
        }

        return message.Length <= 80 ? message : message[..77] + "...";
    }
}
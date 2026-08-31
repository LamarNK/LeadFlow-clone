namespace Orbita.Web.Services;

internal static class LocalChromeAccountStatus
{
    public const string NeedsLogin = "Требуется вход";
    public const string NeedsLoginAvito = "Требуется вход в Avito";
    public const string Ready = "Готов к работе";
    public const string NeedsReLogin = "Требуется повторный вход";
    public const string ProviderOff = "Провайдер выключен";

    public static bool IsFirstLoginRequired(string? status, DateTime? lastMonitoringAt) =>
        string.Equals(status, "RequiresLogin", StringComparison.OrdinalIgnoreCase)
        && lastMonitoringAt is null;

    public static (string Label, string Tone) ForWorkerDetails(
        string? status,
        bool isEnabledInPanel,
        bool providerEnabled,
        DateTime? lastMonitoringAt)
    {
        if (!providerEnabled)
        {
            return (ProviderOff, "inactive");
        }

        if (IsFirstLoginRequired(status, lastMonitoringAt))
        {
            return (NeedsLoginAvito, "warning");
        }

        if (string.Equals(status, "RequiresLogin", StringComparison.OrdinalIgnoreCase))
        {
            return (NeedsReLogin, "warning");
        }

        if (string.Equals(status, "RequiresManualAction", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase))
        {
            return (NeedsReLogin, "error");
        }

        _ = isEnabledInPanel;
        return (Ready, "success");
    }

    public static string OpenBrowserLabel(string? status, DateTime? lastMonitoringAt) =>
        IsFirstLoginRequired(status, lastMonitoringAt)
            ? "Открыть браузер для входа"
            : "Открыть браузер";
}

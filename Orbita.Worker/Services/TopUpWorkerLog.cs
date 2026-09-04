using LeadFlow.Core.Logging.Audit;

namespace Orbita.Worker.Services;

/// <summary>
/// Логирование сессий ручного пополнения аванса на воркере.
/// QR-данные (base64/URL) сюда никогда не попадают.
/// </summary>
internal static class TopUpWorkerLog
{
    public static Task InfoAsync(string message, string memberName, Dictionary<string, object?>? extra = null) =>
        WriteAsync(message, DeskLinkAuditLogLevel.Info, memberName, extra);

    public static Task WarningAsync(string message, string memberName, Dictionary<string, object?>? extra = null) =>
        WriteAsync(message, DeskLinkAuditLogLevel.Warning, memberName, extra);

    public static Task ErrorAsync(string message, string memberName, Dictionary<string, object?>? extra = null) =>
        WriteAsync(message, DeskLinkAuditLogLevel.Error, memberName, extra);

    private static Task WriteAsync(
        string message,
        DeskLinkAuditLogLevel level,
        string memberName,
        Dictionary<string, object?>? extra)
    {
        var properties = new Dictionary<string, object?>
        {
            ["topup.worker"] = true,
            ["worker.pid"] = Environment.ProcessId,
            ["worker.version"] = ApplicationVersionProvider.GetVersion()
        };

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                properties[key] = value;
            }
        }

        return GlobalLogger.Instance.LogAsync(
            message,
            level,
            errorKey: "topup.worker",
            memberName: memberName,
            filePath: "TopUpWorkerLog.cs",
            properties: properties);
    }
}

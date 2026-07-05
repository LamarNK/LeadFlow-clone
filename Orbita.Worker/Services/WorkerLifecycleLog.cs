using LeadFlow.Core.Logging.Audit;

namespace Orbita.Worker.Services;

internal static class WorkerLifecycleLog
{
    public static Task InfoAsync(
        string message,
        string memberName,
        Dictionary<string, object?>? extra = null) =>
        WriteAsync(message, DeskLinkAuditLogLevel.Info, memberName, extra);

    public static Task WarningAsync(
        string message,
        string memberName,
        Dictionary<string, object?>? extra = null) =>
        WriteAsync(message, DeskLinkAuditLogLevel.Warning, memberName, extra);

    public static Task ErrorAsync(
        string message,
        string memberName,
        Dictionary<string, object?>? extra = null) =>
        WriteAsync(message, DeskLinkAuditLogLevel.Error, memberName, extra);

    private static Task WriteAsync(
        string message,
        DeskLinkAuditLogLevel level,
        string memberName,
        Dictionary<string, object?>? extra)
    {
        var properties = CreateBaseProperties();
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
            memberName: memberName,
            filePath: "WorkerLifecycleLog.cs",
            properties: properties);
    }

    private static Dictionary<string, object?> CreateBaseProperties() =>
        new()
        {
            ["worker.pid"] = Environment.ProcessId,
            ["worker.version"] = ApplicationVersionProvider.GetVersion(),
            ["worker.exe"] = Environment.ProcessPath ?? string.Empty,
            ["worker.machine"] = Environment.MachineName,
            ["worker.workingDir"] = AppContext.BaseDirectory
        };
}
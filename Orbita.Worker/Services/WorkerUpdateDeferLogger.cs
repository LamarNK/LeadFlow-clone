using LeadFlow.Core.Logging.Audit;

namespace Orbita.Worker.Services;

internal static class WorkerUpdateDeferLogger
{
    private static readonly Lock Sync = new();
    private static DateTime _lastLogUtc = DateTime.MinValue;
    private static string? _lastReasonKey;
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(2);

    public static void LogDeferredInstall(string caller, string pendingVersion, string? phase, bool monitoringActive)
    {
        var reasonKey = $"{pendingVersion}|{phase ?? "—"}|{monitoringActive}";
        var now = DateTime.UtcNow;
        lock (Sync)
        {
            if (string.Equals(reasonKey, _lastReasonKey, StringComparison.Ordinal)
                && now - _lastLogUtc < LogInterval)
            {
                return;
            }

            _lastReasonKey = reasonKey;
            _lastLogUtc = now;
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"Worker update: MSI {pendingVersion} готов, установка отложена (фаза «{phase ?? "—"}», мониторинг={(monitoringActive ? "активен" : "остановлен")}).",
            DeskLinkAuditLogLevel.Info,
            memberName: caller);
    }
}
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services.Worker;

namespace Orbita.Worker.Services;

public sealed class WorkerPendingUpdateCoordinator(
    WorkerUpdateStore updateStore,
    WorkerUpdateGate updateGate,
    WorkerShutdownService shutdownService,
    WorkerRuntimeState runtimeState) : IWorkerPendingUpdateCoordinator
{
    public bool HasPendingInstall => updateStore.TryGetPendingMsi() is not null;

    public string? PendingVersion => updateStore.TryGetPendingMsi()?.Version;

    public string? BuildWaitingMessage(TimeSpan delay)
    {
        var pending = updateStore.TryGetPendingMsi();
        if (pending is null)
        {
            return null;
        }

        return $"Пауза · установка обновления {pending.Version}";
    }

    public bool TryApplyPendingInstallAtPause()
    {
        var pending = updateStore.TryGetPendingMsi();
        if (pending is null)
        {
            return false;
        }

        if (!updateGate.IsSafeToApply)
        {
            var (phase, monitoringActive) = updateGate.GetSnapshot();
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker update: MSI {pending.Version} готов, установка отложена (фаза «{phase ?? "—"}», мониторинг={(monitoringActive ? "активен" : "остановлен")}).",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(TryApplyPendingInstallAtPause));
            return false;
        }

        runtimeState.Status = "Обновление";
        runtimeState.Detail = $"Установка {pending.Version}";
        if (shutdownService.RequestInstall(pending.MsiPath))
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker update: запуск установки {pending.Version}.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(TryApplyPendingInstallAtPause));
            return true;
        }

        if (!File.Exists(pending.MsiPath))
        {
            updateStore.ClearPendingMsi();
            runtimeState.Status = "Онлайн";
            runtimeState.Detail = $"Обновление {pending.Version}: MSI не найден, ждём повторную загрузку";
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker update: MSI {pending.Version} не найден на диске, ждём повторную загрузку.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(TryApplyPendingInstallAtPause));
        }
        else
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker update: не удалось запустить установку {pending.Version} (возможно, уже идёт перезапуск).",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(TryApplyPendingInstallAtPause));
        }

        return false;
    }
}
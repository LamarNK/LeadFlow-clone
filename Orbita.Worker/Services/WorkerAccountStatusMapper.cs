using LeadFlow.Core.Models;

namespace Orbita.Worker.Services;

internal static class WorkerAccountStatusMapper
{
    public static string ToSnapshotStatus(AvitoAccountStatus status, bool isEnabledInPanel) =>
        !isEnabledInPanel
            ? "Paused"
            : status switch
            {
                AvitoAccountStatus.Error => "Error",
                AvitoAccountStatus.RequiresLogin => "RequiresLogin",
                AvitoAccountStatus.RequiresManualAction => "RequiresManualAction",
                AvitoAccountStatus.Monitoring => "Monitoring",
                AvitoAccountStatus.Authorized => "Active",
                AvitoAccountStatus.Paused => "Paused",
                AvitoAccountStatus.NotConfigured => "Inactive",
                _ => status.ToString()
            };
}
using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Worker;

public static class WorkerAccountStatusMapper
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

    public static AvitoAccountStatus FromSnapshotStatus(string? status) =>
        status switch
        {
            "Error" => AvitoAccountStatus.Error,
            "RequiresLogin" => AvitoAccountStatus.RequiresLogin,
            "RequiresManualAction" => AvitoAccountStatus.RequiresManualAction,
            "Monitoring" => AvitoAccountStatus.Monitoring,
            "Active" => AvitoAccountStatus.Authorized,
            "Paused" => AvitoAccountStatus.Paused,
            "Inactive" => AvitoAccountStatus.NotConfigured,
            _ => Enum.TryParse<AvitoAccountStatus>(status, out var parsed)
                ? parsed
                : AvitoAccountStatus.NotConfigured
        };

    /// <summary>
    /// «Paused» в снимке Orbita часто означает «выключен в панели», а не реальную паузу мониторинга.
    /// </summary>
    public static AvitoAccountStatus ResolveRuntimeStatus(bool isEnabledInPanel, string? snapshotStatus)
    {
        var status = FromSnapshotStatus(snapshotStatus);
        if (isEnabledInPanel && status == AvitoAccountStatus.Paused)
        {
            return AvitoAccountStatus.Authorized;
        }

        return status;
    }
}
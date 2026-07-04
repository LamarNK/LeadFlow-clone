namespace LeadFlow.Core.Services.Worker;

public interface IWorkerPendingUpdateCoordinator
{
    bool HasPendingInstall { get; }

    string? PendingVersion { get; }

    string? BuildWaitingMessage(TimeSpan delay);

    bool TryApplyPendingInstallAtPause();
}
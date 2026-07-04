namespace LeadFlow.Core.Services.Worker;

public sealed class NullWorkerPendingUpdateCoordinator : IWorkerPendingUpdateCoordinator
{
    public bool HasPendingInstall => false;

    public string? PendingVersion => null;

    public string? BuildWaitingMessage(TimeSpan delay) => null;

    public bool TryApplyPendingInstallAtPause() => false;
}
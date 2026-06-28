namespace LeadFlow.Core.Models;

public enum MonitoringStatus
{
    Waiting,
    Running,
    Recovering,
    RequiresAuthorization,
    RequiresManualAction,
    Error,
    Stopped
}

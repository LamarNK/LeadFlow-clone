namespace LeadFlow.Models;

public enum MonitoringStatus
{
    Waiting,
    Running,
    RequiresAuthorization,
    RequiresManualAction,
    Error,
    Stopped
}

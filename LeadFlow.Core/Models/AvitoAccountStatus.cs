namespace LeadFlow.Core.Models;

public enum AvitoAccountStatus
{
    NotConfigured,
    RequiresLogin,
    Authorized,
    Monitoring,
    Paused,
    RequiresManualAction,
    Error
}

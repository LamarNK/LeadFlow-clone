namespace LeadFlow.Data;

public sealed class AvitoAccountEntity
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string AvitoResponsesUrl { get; set; } = string.Empty;
    public string BrowserProfilePath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? LastAuthCheckAt { get; set; }
    public DateTime? LastMonitoringAt { get; set; }
    public string LastErrorMessage { get; set; } = string.Empty;
}

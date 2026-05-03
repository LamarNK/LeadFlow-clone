namespace LeadFlow.Models;

public sealed class AvitoAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string AvitoResponsesUrl { get; set; } = "https://www.avito.ru";
    public string BrowserProfilePath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public AvitoAccountStatus Status { get; set; } = AvitoAccountStatus.NotConfigured;
    public DateTime? LastAuthCheckAt { get; set; }
    public DateTime? LastMonitoringAt { get; set; }
    public string LastErrorMessage { get; set; } = string.Empty;
}

namespace LeadFlow.Models;

public sealed class AccountMonitoringState
{
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public AvitoAccountStatus Status { get; set; } = AvitoAccountStatus.NotConfigured;
    public DateTime? LastCheckedAt { get; set; }
    public string LastMessage { get; set; } = string.Empty;
}

namespace LeadFlow.Core.Models;

public sealed class ProcessingLogItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CandidateResponseId { get; set; }
    public Guid AccountId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}

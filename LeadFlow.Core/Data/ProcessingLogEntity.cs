namespace LeadFlow.Core.Data;

public sealed class ProcessingLogEntity
{
    public Guid Id { get; set; }
    public Guid? CandidateResponseId { get; set; }
    public Guid AccountId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}

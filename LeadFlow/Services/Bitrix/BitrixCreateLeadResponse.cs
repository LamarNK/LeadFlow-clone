namespace LeadFlow.Services.Bitrix;

public sealed class BitrixCreateLeadResponse
{
    public bool IsSuccess { get; set; }
    public string EntityId { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

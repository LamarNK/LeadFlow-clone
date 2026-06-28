namespace LeadFlow.Core.Services.Bitrix;

public sealed class BitrixCreateLeadResponse
{
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Сознательно не вызывали Bitrix24 (флаг <see cref="BitrixClient.DealCreationTemporarilyDisabled"/>).
    /// </summary>
    public bool BitrixCreationSuppressed { get; set; }

    public string EntityId { get; set; } = string.Empty;
    public string ContactId { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

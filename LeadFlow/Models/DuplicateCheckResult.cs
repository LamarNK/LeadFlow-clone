namespace LeadFlow.Models;

public sealed class DuplicateCheckResult
{
    public string PhoneRaw { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    public bool IsLocalDuplicate { get; set; }
    public bool IsBitrixDuplicate { get; set; }
    public bool IsDuplicate => IsLocalDuplicate || IsBitrixDuplicate;
    public string Summary =>
        IsDuplicate
            ? "Дубль найден — сделка не создаётся"
            : "Дубль не найден — сделка будет создана автоматически";
}

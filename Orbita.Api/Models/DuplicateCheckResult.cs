namespace Orbita.Api.Models;

public sealed class DuplicateCheckResult
{
    public string PhoneRaw { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    public bool IsLocalDuplicate { get; set; }
    public bool IsBitrixDuplicate { get; set; }
    public bool IsBitrixCheckUnavailable { get; set; }
    public string? BitrixCheckUnavailableReason { get; set; }

    public bool IsDuplicate => IsLocalDuplicate || IsBitrixDuplicate;

    public bool ShouldDeferBitrixSend => IsBitrixCheckUnavailable;

    public string Summary =>
        IsBitrixCheckUnavailable
            ? $"Проверка дублей в Bitrix24 недоступна — отправка в CRM отложена. {BitrixCheckUnavailableReason}".Trim()
            : IsDuplicate
                ? "Дубль найден — сделка не создаётся"
                : "Дубль не найден — сделка будет создана автоматически";
}
namespace LeadFlow.Models;

public sealed class SubProfileBalanceItem
{
    public Guid AccountId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public string SubProfileName { get; init; } = string.Empty;
    public decimal? Balance { get; init; }

    public string BalanceDisplay => Balance is decimal b
        ? $"{b:N2} ₽"
        : "—";

    public bool HasBalance => Balance.HasValue;

    public bool IsLowBalance => Balance is decimal b && b < BalanceDisplayRules.LowBalanceThresholdRub;

    public string SubProfileLine => $"{SubProfileName}={BalanceDisplay}";
}

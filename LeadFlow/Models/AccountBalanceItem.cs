namespace LeadFlow.Models;

public sealed class AccountBalanceItem
{
    public Guid AccountId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public decimal TotalBalance { get; init; }
    public IReadOnlyList<SubProfileBalanceItem> SubProfiles { get; init; } = Array.Empty<SubProfileBalanceItem>();

    public string TotalBalanceDisplay => $"{TotalBalance:N2} ₽";
    public bool HasBalance => SubProfiles.Any(s => s.HasBalance);
    public double BarWidth { get; set; }
}

namespace LeadFlow.Core.Models;

public sealed class SubProfileBalanceItem
{
    public Guid AccountId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public string SubProfileName { get; init; } = string.Empty;
    public decimal? Balance { get; init; }
    public decimal? WalletBalance { get; init; }
    public string? AdvanceDurationText { get; init; }

    public string BalanceDisplay
    {
        get
        {
            var parts = new List<string>(2);
            if (Balance is decimal advance)
            {
                var line = $"Аванс {advance:N2} ₽";
                if (!string.IsNullOrWhiteSpace(AdvanceDurationText))
                {
                    line += $" · {AdvanceDurationText}";
                }

                parts.Add(line);
            }

            if (WalletBalance is decimal wallet)
            {
                parts.Add($"Кош. {wallet:N2} ₽");
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : "—";
        }
    }

    public bool HasBalance => Balance.HasValue;

    public bool IsLowBalance => Balance is decimal b && b < BalanceDisplayRules.LowBalanceThresholdRub;

    public string SubProfileLine => $"{SubProfileName}={BalanceDisplay}";
}

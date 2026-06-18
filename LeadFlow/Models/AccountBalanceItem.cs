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

    public int SubProfileCount => SubProfiles.Count;

    public decimal? MinSubProfileBalance
    {
        get
        {
            var values = SubProfiles.Where(s => s.Balance.HasValue).Select(s => s.Balance!.Value).ToList();
            return values.Count > 0 ? values.Min() : null;
        }
    }

    public bool HasLowBalance =>
        HasBalance && (TotalBalance < BalanceDisplayRules.LowBalanceThresholdRub
                       || (MinSubProfileBalance ?? decimal.MaxValue) < BalanceDisplayRules.LowBalanceThresholdRub);

    /// <summary>Одна строка для компактного блока на Dashboard (без перечисления 20+ субпрофилей).</summary>
    public string CompactSubProfileSummary
    {
        get
        {
            if (SubProfiles.Count == 0)
            {
                return "нет данных";
            }

            if (SubProfiles.Count == 1)
            {
                return SubProfiles[0].SubProfileLine;
            }

            if (SubProfiles.Count <= 3)
            {
                return string.Join(" · ", SubProfiles.Select(static s => $"{s.SubProfileName} {s.BalanceDisplay}"));
            }

            var min = MinSubProfileBalance;
            return min.HasValue
                ? $"{SubProfiles.Count} субпроф. · мин {min.Value:N0} ₽"
                : $"{SubProfiles.Count} субпроф.";
        }
    }
}

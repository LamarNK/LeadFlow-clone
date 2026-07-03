namespace Orbita.Web.Formatting;

public static class BalanceDisplay
{
    public static string FormatAmount(decimal? value) =>
        value.HasValue ? $"{value.Value:N0} ₽" : "—";

    public static string FormatSubProfile(
        decimal? walletBalance,
        decimal? advanceBalance,
        string? advanceDurationText)
    {
        var lines = new List<string>(2);

        if (advanceBalance.HasValue)
        {
            var line = $"Аванс {FormatAmount(advanceBalance)}";
            if (!string.IsNullOrWhiteSpace(advanceDurationText))
            {
                line += $" · {advanceDurationText.Trim()}";
            }

            lines.Add(line);
        }

        if (walletBalance.HasValue)
        {
            lines.Add($"Кош. {FormatAmount(walletBalance)}");
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "—";
    }

    public static string? FormatAccountBreakdown(
        decimal? walletTotal,
        string? advanceDurationHint)
    {
        var parts = new List<string>(2);

        if (walletTotal.HasValue)
        {
            parts.Add($"Кош. {FormatAmount(walletTotal)}");
        }

        if (!string.IsNullOrWhiteSpace(advanceDurationHint))
        {
            parts.Add(advanceDurationHint.Trim());
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    public static string? ResolveAdvanceDurationHint(
        IReadOnlyList<(decimal? AdvanceBalance, string? AdvanceDurationText)> items)
    {
        if (items.Count == 0)
        {
            return null;
        }

        if (items.Count == 1)
        {
            return string.IsNullOrWhiteSpace(items[0].AdvanceDurationText)
                ? null
                : items[0].AdvanceDurationText;
        }

        var withDuration = items
            .Where(x => x.AdvanceBalance.HasValue && !string.IsNullOrWhiteSpace(x.AdvanceDurationText))
            .ToList();

        if (withDuration.Count == 0)
        {
            return null;
        }

        if (withDuration.Count == 1)
        {
            return withDuration[0].AdvanceDurationText;
        }

        return $"{withDuration.Count} субпроф. с оценкой срока";
    }
}
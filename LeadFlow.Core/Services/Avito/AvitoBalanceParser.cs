using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.Avito;

public static class AvitoBalanceParser
{
    private static readonly Regex AdvanceBalanceRegex = new(
        @"<p[^>]*>\s*Аванс\s*</p>\s*<h5[^>]*>(?<balance>[^<]+)</h5>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MoneySidebarAdvanceRegex = new(
        @"data-marker=""osp-sidebar/tools/money""[\s\S]*?<p[^>]*>\s*Аванс\s*</p>\s*<h5[^>]*>(?<balance>[^<]+)</h5>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static decimal? ParseAdvanceBalance(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var match = AdvanceBalanceRegex.Match(html);
        if (!match.Success)
        {
            match = MoneySidebarAdvanceRegex.Match(html);
            if (!match.Success)
            {
                return null;
            }
        }

        var raw = match.Groups["balance"].Value;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = NormalizeBalanceToken(raw);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (decimal.TryParse(
                raw,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),
                out var result))
        {
            return result;
        }

        return null;
    }

    private static string NormalizeBalanceToken(string raw) =>
        raw
            .Replace("&nbsp;", "", StringComparison.Ordinal)
            .Replace("\u00a0", "", StringComparison.Ordinal)
            .Replace("&thinsp;", "", StringComparison.Ordinal)
            .Replace("₽", "", StringComparison.Ordinal)
            .Replace("руб.", "", StringComparison.OrdinalIgnoreCase)
            .Replace("руб", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "", StringComparison.Ordinal)
            .Trim();
}

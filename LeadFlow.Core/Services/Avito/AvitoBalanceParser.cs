using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.Avito;

public static class AvitoBalanceParser
{
    private static readonly Regex AdvanceBalanceRegex = new(
        @"<p[^>]*>Аванс</p>\s*<h5[^>]*>(?<balance>[^<]+)</h5>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static decimal? ParseAdvanceBalance(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var match = AdvanceBalanceRegex.Match(html);
        if (!match.Success)
            return null;

        var raw = match.Groups["balance"].Value;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw
            .Replace("&nbsp;", "", StringComparison.Ordinal)
            .Replace("\u00a0", "")
            .Replace(" ", "")
            .Replace("&thinsp;", "", StringComparison.Ordinal)
            .Trim();

        if (decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), out var result))
            return result;

        return null;
    }
}

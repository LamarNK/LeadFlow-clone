using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.Avito;

public static class AvitoBalanceParser
{
    private static readonly Regex MoneyTileRegex = new(
        @"<p[^>]*>\s*(?<label>Кошел[её]к|Аванс)\s*</p>\s*<h5[^>]*>(?<balance>[^<]+)</h5>(?:\s*<p[^>]*>(?<subtitle>[^<]*)</p>)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AdvanceBalanceRegex = new(
        @"<p[^>]*>\s*Аванс\s*</p>\s*<h5[^>]*>(?<balance>[^<]+)</h5>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MoneySidebarAdvanceRegex = new(
        @"data-marker=""osp-sidebar/tools/money""[\s\S]*?<p[^>]*>\s*Аванс\s*</p>\s*<h5[^>]*>(?<balance>[^<]+)</h5>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ProfileRatingRegex = new(
        @"data-marker=""osp-sidebar/tools/stats/rating""[\s\S]*?<strong[^>]*>\s*(?<rating>[\d]+(?:[.,]\d+)?)\s*</strong>[\s\S]*?<p[^>]*>\s*(?<reviews>[^<]+)\s*</p>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReviewsCountRegex = new(
        @"(?<count>\d+)\s+отзыв",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AvitoMoneySidebar? ParseMoneySidebar(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var hasMoneyMarker = html.Contains("osp-sidebar/tools/money", StringComparison.OrdinalIgnoreCase);
        var hasRatingMarker = html.Contains("osp-sidebar/tools/stats/rating", StringComparison.OrdinalIgnoreCase);
        if (!hasMoneyMarker && !hasRatingMarker)
        {
            return null;
        }

        decimal? wallet = null;
        decimal? advance = null;
        string? advanceDuration = null;

        foreach (Match match in MoneyTileRegex.Matches(html))
        {
            var label = match.Groups["label"].Value;
            var balance = ParseBalanceToken(match.Groups["balance"].Value);
            var subtitle = match.Groups["subtitle"].Success
                ? NormalizeSubtitle(match.Groups["subtitle"].Value)
                : null;

            if (label.Contains("Кошел", StringComparison.OrdinalIgnoreCase))
            {
                wallet = balance;
                continue;
            }

            if (label.Contains("Аванс", StringComparison.OrdinalIgnoreCase))
            {
                advance = balance;
                if (!string.IsNullOrWhiteSpace(subtitle)
                    && subtitle.Contains("дн", StringComparison.OrdinalIgnoreCase))
                {
                    advanceDuration = subtitle;
                }
            }
        }

        var (rating, reviewsCount, reviewsText) = ParseProfileRating(html);

        if (wallet is null && advance is null && rating is null)
        {
            return null;
        }

        return new AvitoMoneySidebar(wallet, advance, advanceDuration, rating, reviewsCount, reviewsText);
    }

    private static (decimal? Rating, int? ReviewsCount, string? ReviewsText) ParseProfileRating(string html)
    {
        if (!html.Contains("osp-sidebar/tools/stats/rating", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, null);
        }

        var match = ProfileRatingRegex.Match(html);
        if (!match.Success)
        {
            return (null, null, null);
        }

        var rating = ParseRatingToken(match.Groups["rating"].Value);
        var reviewsRaw = NormalizeSubtitle(match.Groups["reviews"].Value);
        if (string.IsNullOrWhiteSpace(reviewsRaw))
        {
            return (rating, null, null);
        }

        int? reviewsCount = null;
        var countMatch = ReviewsCountRegex.Match(reviewsRaw);
        if (countMatch.Success
            && int.TryParse(countMatch.Groups["count"].Value, out var parsedCount))
        {
            reviewsCount = parsedCount;
        }
        else if (reviewsRaw.Contains("нет отзыв", StringComparison.OrdinalIgnoreCase))
        {
            reviewsCount = 0;
        }

        return (rating, reviewsCount, reviewsRaw);
    }

    public static decimal? ParseAdvanceBalance(string? html) =>
        ParseMoneySidebar(html)?.AdvanceBalance ?? ParseAdvanceBalanceLegacy(html);

    private static decimal? ParseAdvanceBalanceLegacy(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = AdvanceBalanceRegex.Match(html);
        if (!match.Success)
        {
            match = MoneySidebarAdvanceRegex.Match(html);
            if (!match.Success)
            {
                return null;
            }
        }

        return ParseBalanceToken(match.Groups["balance"].Value);
    }

    private static decimal? ParseBalanceToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = NormalizeBalanceToken(raw);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return decimal.TryParse(
            raw,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),
            out var result)
            ? result
            : null;
    }

    private static string NormalizeSubtitle(string raw) =>
        raw
            .Replace("&nbsp;", " ", StringComparison.Ordinal)
            .Replace("\u00a0", " ", StringComparison.Ordinal)
            .Replace("&thinsp;", " ", StringComparison.Ordinal)
            .Trim();

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

    private static decimal? ParseRatingToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.Trim().Replace(',', '.');
        return decimal.TryParse(
            raw,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var result)
            ? result
            : null;
    }
}
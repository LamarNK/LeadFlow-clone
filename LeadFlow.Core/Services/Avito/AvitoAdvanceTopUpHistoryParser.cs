using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Парсит операции из страницы Avito «Кошелёк → История операций».
/// История является источником истины о факте оплаты: аванс на странице Avito
/// может обновиться позже, а операция «Внесение аванса» появляется сразу.
/// </summary>
public static class AvitoAdvanceTopUpHistoryParser
{
    private static readonly Regex OperationMarkerRegex = new(
        "data-marker\\s*=\\s*[\"']operation[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AmountRegex = new(
        @"[−-]\s*(?<amount>[\d\s\u00a0]+(?:[.,]\d+)?)\s*₽",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DateRegex = new(
        @"(?<day>\d{1,2})\s+(?<month>января|февраля|марта|апреля|мая|июня|июля|августа|сентября|октября|ноября|декабря)\s*,\s*(?<hour>\d{1,2}):(?<minute>\d{2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, int> MonthNumbers =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["января"] = 1,
            ["февраля"] = 2,
            ["марта"] = 3,
            ["апреля"] = 4,
            ["мая"] = 5,
            ["июня"] = 6,
            ["июля"] = 7,
            ["августа"] = 8,
            ["сентября"] = 9,
            ["октября"] = 10,
            ["ноября"] = 11,
            ["декабря"] = 12
        };

    public static bool IsHistoryPage(string? html) =>
        !string.IsNullOrWhiteSpace(html)
        && (html.Contains("tabs/tab(operations-history)", StringComparison.OrdinalIgnoreCase)
            || html.Contains("История операций", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<AvitoWalletHistoryOperation> Parse(
        string? html,
        DateTime capturedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var operationMarkers = OperationMarkerRegex.Matches(html);
        if (operationMarkers.Count == 0)
        {
            return [];
        }

        var result = new List<AvitoWalletHistoryOperation>(operationMarkers.Count);
        for (var i = 0; i < operationMarkers.Count; i++)
        {
            var start = operationMarkers[i].Index;
            var end = i + 1 < operationMarkers.Count
                ? operationMarkers[i + 1].Index
                : html.Length;
            var text = StripHtml(html[start..end]);
            if (!text.Contains("Внесение аванса", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var amountMatch = AmountRegex.Match(text);
            var dateMatch = DateRegex.Match(text);
            if (!amountMatch.Success
                || !dateMatch.Success
                || !decimal.TryParse(
                    amountMatch.Groups["amount"].Value
                        .Replace(" ", string.Empty, StringComparison.Ordinal)
                        .Replace("\u00a0", string.Empty, StringComparison.Ordinal)
                        .Replace(',', '.'),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var amount)
                || !TryParseOccurredAtUtc(dateMatch, capturedAtUtc, out var occurredAtUtc))
            {
                continue;
            }

            result.Add(new AvitoWalletHistoryOperation(
                "Внесение аванса",
                amount,
                occurredAtUtc));
        }

        return result;
    }

    public static AvitoWalletHistoryOperation? FindMatchingAdvanceTopUp(
        string? html,
        decimal requestedAmount,
        DateTime paymentClaimedAtUtc,
        DateTime capturedAtUtc)
    {
        var lowerBound = paymentClaimedAtUtc.AddMinutes(-2);
        var upperBound = capturedAtUtc.AddMinutes(2);
        return Parse(html, capturedAtUtc)
            .Where(x =>
                Math.Abs(x.Amount - requestedAmount) <= 1m
                && x.OccurredAtUtc >= lowerBound
                && x.OccurredAtUtc <= upperBound)
            .OrderBy(x => x.OccurredAtUtc)
            .FirstOrDefault();
    }

    private static bool TryParseOccurredAtUtc(
        Match match,
        DateTime capturedAtUtc,
        out DateTime occurredAtUtc)
    {
        occurredAtUtc = default;
        if (!MonthNumbers.TryGetValue(match.Groups["month"].Value, out var month)
            || !int.TryParse(match.Groups["day"].Value, out var day)
            || !int.TryParse(match.Groups["hour"].Value, out var hour)
            || !int.TryParse(match.Groups["minute"].Value, out var minute))
        {
            return false;
        }

        var capturedLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(capturedAtUtc, DateTimeKind.Utc),
            TopUpSessionRules.MoscowTimeZone);
        var year = capturedLocal.Year;
        if (month > capturedLocal.Month + 1
            || (month == capturedLocal.Month
                && day > capturedLocal.Day
                && capturedLocal.Day <= 2))
        {
            year--;
        }

        try
        {
            var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
            occurredAtUtc = TimeZoneInfo.ConvertTimeToUtc(
                local,
                TopUpSessionRules.MoscowTimeZone);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string StripHtml(string value)
    {
        var withoutTags = Regex.Replace(value, "<[^>]+>", " ");
        return Regex.Replace(
            WebUtility.HtmlDecode(withoutTags)
                .Replace('\u00a0', ' '),
            @"\s+",
            " ").Trim();
    }
}

public sealed record AvitoWalletHistoryOperation(
    string Description,
    decimal Amount,
    DateTime OccurredAtUtc);

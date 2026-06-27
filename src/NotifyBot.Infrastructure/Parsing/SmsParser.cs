using System.Text.RegularExpressions;
using NotifyBot.Application.Abstractions;
using NotifyBot.Domain.Models;

namespace NotifyBot.Infrastructure.Parsing;

public sealed partial class SmsParser : ISmsParser
{
    [GeneratedRegex(
        @"Для оплаты в (?<merchant>\S+) (?<amount>[\d,]+\.\d+) RUB Карта \*(?<card>\d{4}); 3DS код: (?<code>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PaymentSmsRegex();

    public SmsInfo? Parse(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var match = PaymentSmsRegex().Match(rawText.Trim());
        if (!match.Success)
        {
            return null;
        }

        return new SmsInfo(
            CardLast4: match.Groups["card"].Value,
            Code: match.Groups["code"].Value,
            Amount: match.Groups["amount"].Value,
            Merchant: match.Groups["merchant"].Value,
            RawText: rawText.Trim());
    }
}
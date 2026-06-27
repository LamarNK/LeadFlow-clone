namespace NotifyBot.Domain.Models;

public sealed record SmsInfo(
    string CardLast4,
    string Code,
    string Amount,
    string Merchant,
    string RawText);
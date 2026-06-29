using NotifyBot.Application.Abstractions;
using NotifyBot.Domain.Models;

namespace NotifyBot.Application.Services;

public static class SmsMessageFormatter
{
    public static string Format3ds(SmsInfo smsInfo, DateTimeOffset? receivedAtUtc = null)
    {
        var time = receivedAtUtc is { } at
            ? $"[{at.ToLocalTime():dd.MM.yyyy HH:mm:ss}]\n"
            : string.Empty;

        return
            $"{time}3DS код: {smsInfo.Code}\n" +
            $"Сумма: {smsInfo.Amount} RUB\n" +
            $"Магазин: {smsInfo.Merchant}\n" +
            $"Карта: *{smsInfo.CardLast4}";
    }

    public static string FormatSmsBlock(PlusofonSmsMessage message)
    {
        var when = message.ReceivedAtUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") ?? "?";
        var direction = message.Incoming ? "вх" : "исх";
        return
            $"[{when}] {direction}\n" +
            $"от: {message.Sender ?? "?"}\n" +
            $"кому: {message.Receiver ?? "?"}\n" +
            message.Text;
    }

}
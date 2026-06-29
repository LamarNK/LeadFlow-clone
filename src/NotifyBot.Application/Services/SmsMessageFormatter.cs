using NotifyBot.Domain.Models;

namespace NotifyBot.Application.Services;

public static class SmsMessageFormatter
{
    public static string Format3ds(SmsInfo smsInfo) =>
        $"3DS код: {smsInfo.Code}\n" +
        $"Сумма: {smsInfo.Amount} RUB\n" +
        $"Магазин: {smsInfo.Merchant}\n" +
        $"Карта: *{smsInfo.CardLast4}";
}
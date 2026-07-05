using System.Globalization;

namespace NotifyBot.Application.Services;

/// <summary>
/// User-facing time is always Moscow (UTC+3), regardless of server/container timezone.
/// </summary>
public static class NotifyBotTime
{
    public static readonly TimeSpan MoscowOffset = TimeSpan.FromHours(3);

    public static DateTimeOffset ToMoscow(DateTimeOffset utc) => utc.ToOffset(MoscowOffset);

    public static string FormatMoscow(DateTimeOffset utc, string format = "dd.MM.yyyy HH:mm:ss") =>
        ToMoscow(utc).ToString(format, CultureInfo.InvariantCulture);
}
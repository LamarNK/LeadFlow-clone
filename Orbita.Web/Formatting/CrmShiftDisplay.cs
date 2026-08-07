using System.Globalization;
using System.Text;

namespace Orbita.Web.Formatting;

/// <summary>Отображение CRM-смены менеджера в UI (длительность, «вышел»).</summary>
public static class CrmShiftDisplay
{
    /// <summary>
    /// Вторая строка под именем: «на смене · 2 ч 15 мин · с 09:40» или «не на смене · вышел 08.08, 18:40».
    /// </summary>
    public static string FormatStatusLine(
        bool isShiftActive,
        DateTime? shiftStartedAtUtc,
        DateTime? lastShiftEndedAtUtc,
        DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        if (isShiftActive)
        {
            if (shiftStartedAtUtc is DateTime started)
            {
                var duration = FormatDuration(now - NormalizeUtc(started));
                var since = NormalizeUtc(started).ToLocalTime().ToString("HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
                return string.IsNullOrEmpty(duration)
                    ? $"на смене · с {since}"
                    : $"на смене · {duration} · с {since}";
            }

            return "на смене";
        }

        if (lastShiftEndedAtUtc is DateTime ended)
        {
            return $"не на смене · вышел {FormatEndedAt(ended)}";
        }

        return "не на смене";
    }

    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        var totalMinutes = (int)Math.Floor(span.TotalMinutes);
        if (totalMinutes < 1)
        {
            return "меньше минуты";
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        var sb = new StringBuilder();
        if (hours > 0)
        {
            sb.Append(hours).Append(" ч");
        }

        if (minutes > 0)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(minutes).Append(" мин");
        }

        return sb.ToString();
    }

    public static string FormatEndedAt(DateTime endedAtUtc)
    {
        var local = NormalizeUtc(endedAtUtc).ToLocalTime();
        var today = DateTime.Now.Date;
        if (local.Date == today)
        {
            return "сегодня, " + local.ToString("HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        }

        if (local.Date == today.AddDays(-1))
        {
            return "вчера, " + local.ToString("HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        }

        return local.ToString("dd.MM, HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}

using System.Text;

namespace Orbita.Web.Formatting;

/// <summary>Отображение CRM-смены менеджера в UI (длительность, «вышел»).</summary>
public static class CrmShiftDisplay
{
    public static string FormatDurationSince(DateTime startedAtUtc, DateTime? utcNow = null) =>
        FormatDuration(NormalizeUtc(utcNow ?? DateTime.UtcNow) - NormalizeUtc(startedAtUtc));

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

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}

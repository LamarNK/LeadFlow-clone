namespace Orbita.Contracts;

public static class WorkerScheduleRules
{
    public static readonly string[] AllowedDays = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private static readonly TimeSpan MoscowOffset = TimeSpan.FromHours(3);

    public static string NormalizeDaysCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return string.Empty;
        }

        var selected = csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => AllowedDays.Contains(x, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return string.Join(',', selected);
    }

    public static string? NormalizeTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return TimeOnly.TryParse(value.Trim(), out var parsed)
            ? parsed.ToString("HH:mm")
            : null;
    }

    public static bool IsActiveNow(
        bool enabled,
        string? daysCsv,
        string? fromLocalTime,
        string? toLocalTime,
        DateTime utcNow)
    {
        if (!enabled)
        {
            return false;
        }

        var normalizedDays = NormalizeDaysCsv(daysCsv);
        var normalizedFrom = NormalizeTime(fromLocalTime);
        var normalizedTo = NormalizeTime(toLocalTime);
        if (string.IsNullOrWhiteSpace(normalizedDays)
            || normalizedFrom is null
            || normalizedTo is null
            || normalizedFrom == normalizedTo)
        {
            return false;
        }

        var localNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc).Add(MoscowOffset);
        var dayCode = ToDayCode(localNow.DayOfWeek);
        var time = TimeOnly.FromDateTime(localNow);
        var from = TimeOnly.ParseExact(normalizedFrom, "HH:mm");
        var to = TimeOnly.ParseExact(normalizedTo, "HH:mm");
        if (from < to)
        {
            return normalizedDays.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(dayCode, StringComparer.Ordinal)
                && time >= from
                && time < to;
        }

        if (time >= from)
        {
            return normalizedDays.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(dayCode, StringComparer.Ordinal);
        }

        if (time < to)
        {
            var previousDay = ToDayCode(localNow.AddDays(-1).DayOfWeek);
            return normalizedDays.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(previousDay, StringComparer.Ordinal);
        }

        return false;
    }

    private static string ToDayCode(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => "Mon",
        DayOfWeek.Tuesday => "Tue",
        DayOfWeek.Wednesday => "Wed",
        DayOfWeek.Thursday => "Thu",
        DayOfWeek.Friday => "Fri",
        DayOfWeek.Saturday => "Sat",
        _ => "Sun"
    };
}

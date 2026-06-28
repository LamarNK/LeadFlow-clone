using System.Globalization;

namespace Orbita.Web.Services;

public sealed record DashboardPeriod(DateTime From, DateTime To)
{
    public const int MaxDays = 366;

    public static DashboardPeriod Today => new(DateTime.Today, DateTime.Today);

    public static DashboardPeriod Parse(string? from, string? to)
    {
        if (!TryParseDate(from, out var parsedFrom) || !TryParseDate(to, out var parsedTo))
        {
            return Today;
        }

        if (parsedFrom > parsedTo)
        {
            (parsedFrom, parsedTo) = (parsedTo, parsedFrom);
        }

        var spanDays = (parsedTo - parsedFrom).Days;
        if (spanDays > MaxDays)
        {
            parsedTo = parsedFrom.AddDays(MaxDays);
        }

        return new DashboardPeriod(parsedFrom, parsedTo);
    }

    public bool IsTodayOnly => From == DateTime.Today && To == DateTime.Today;

    public bool IsSingleDay => From == To;

    public string Label => IsSingleDay
        ? From.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
        : $"{From:dd.MM.yyyy} — {To:dd.MM.yyyy}";

    public string? ActivePreset
    {
        get
        {
            var today = DateTime.Today;
            if (From == today && To == today) return "today";
            if (From == today.AddDays(-1) && To == today.AddDays(-1)) return "yesterday";
            if (From == today.AddDays(-6) && To == today) return "7d";
            if (From == today.AddDays(-13) && To == today) return "14d";
            if (From == today.AddDays(-29) && To == today) return "30d";
            return null;
        }
    }

    public string FromIso => From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string ToIso => To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryParseDate(string? value, out DateTime date)
    {
        date = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateTime.TryParseExact(
                value.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
    }
}
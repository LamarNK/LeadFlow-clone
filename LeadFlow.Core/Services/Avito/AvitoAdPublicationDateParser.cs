using System.Globalization;
using System.Text.RegularExpressions;
using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

public static class AvitoAdPublicationDateParser
{
    private static readonly Regex ItemIdLine = new(
        @"№\s*(?<id>\d+)\s*,\s*(?<day>\d{1,2})\s+(?<month>[А-Яа-яЁё]+)\s+в\s+(?<hour>\d{1,2}):(?<minute>\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
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

    public static bool TryParseItemIdLine(
        string? text,
        DateTime capturedAtUtc,
        out string itemId,
        out DateTime publishedAtUtc,
        out string? error)
    {
        itemId = string.Empty;
        publishedAtUtc = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty_item_id_text";
            return false;
        }

        var match = ItemIdLine.Match(text);
        if (!match.Success)
        {
            error = "item_id_line_unparsed";
            return false;
        }

        itemId = match.Groups["id"].Value;
        if (!int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || !int.TryParse(match.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(match.Groups["minute"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
        {
            error = "item_id_line_numbers";
            return false;
        }

        var monthName = match.Groups["month"].Value.Trim();
        if (!Months.TryGetValue(monthName, out var month))
        {
            error = "unknown_month";
            return false;
        }

        var localNow = AvitoAdBusinessTime.LocalNow(capturedAtUtc);
        var year = localNow.Year;
        if (!TryCreateLocal(year, month, day, hour, minute, out var local))
        {
            error = "invalid_calendar_date";
            return false;
        }

        if (local > localNow)
        {
            if (!TryCreateLocal(year - 1, month, day, hour, minute, out local))
            {
                error = "invalid_previous_year_date";
                return false;
            }
        }

        publishedAtUtc = AvitoAdBusinessTime.ToUtc(local);
        return true;
    }

    public static AvitoAdDetailParseResult ToFailed(string reason, string rawItemIdText = "", string rawLifeBarText = "") =>
        new()
        {
            Success = false,
            FailureReason = reason,
            PublicationDateSource = AvitoAdPublicationDateSources.Unknown,
            RawItemIdText = rawItemIdText,
            RawLifeBarText = rawLifeBarText
        };

    private static bool TryCreateLocal(int year, int month, int day, int hour, int minute, out DateTime local)
    {
        local = default;
        try
        {
            local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

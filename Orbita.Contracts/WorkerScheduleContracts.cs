namespace Orbita.Contracts;

public static class WorkerScheduleShifts
{
    public const string Shift1 = "SHIFT_1";
    public const string Shift2 = "SHIFT_2";
    public const string DayFirst = "DAY_FIRST";
    public const string NightFirst = "NIGHT_FIRST";
    public const string Day = "DAY";
    public const string Night = "NIGHT";
    public const string Off = "OFF";

    public static string Opposite(string value) => value == DayFirst ? NightFirst : DayFirst;
}

public sealed record WorkerScheduleSettingsDto(
    Guid Id,
    Guid OfficeId,
    string DayStartLocalTime,
    string DayEndLocalTime,
    string NightStartLocalTime,
    string NightEndLocalTime,
    string TimeZoneId,
    DateTime UpdatedAtUtc);

public sealed record WorkerScheduleDayDto(
    int DayOfWeek,
    string DayCode,
    string Label,
    string Shift1,
    string Shift2,
    bool IsDayOff);

public sealed record WorkerScheduleWorkerDto(
    Guid WorkerId,
    string DisplayName,
    bool IsEnabled,
    string Shift,
    bool IsOnline,
    bool IsMonitoringPaused,
    string TodayState,
    string? NextWorkingDay,
    DateTime? LastSeenAtUtc);

public sealed record WorkerScheduleGroupDto(
    Guid Id,
    Guid OfficeId,
    int DayOff,
    string Name,
    string CurrentWeekShift,
    DateOnly? LastAppliedOffDate,
    int WorkerCount,
    int Shift1Count,
    int Shift2Count,
    IReadOnlyList<WorkerScheduleWorkerDto> Workers,
    IReadOnlyList<WorkerScheduleDayDto> Calendar);

public sealed record WorkerScheduleCandidateDto(
    Guid WorkerId,
    string DisplayName,
    bool IsEnabled,
    bool IsOnline,
    Guid? GroupId,
    string? GroupName,
    string? Shift);

public sealed record WorkerScheduleOfficeDto(
    WorkerScheduleSettingsDto Settings,
    IReadOnlyList<WorkerScheduleGroupDto> Groups,
    IReadOnlyList<WorkerScheduleCandidateDto> Workers,
    int? SelectedDayOff);

public sealed record AddWorkerScheduleRequest(
    Guid GroupId,
    IReadOnlyList<Guid> WorkerIds,
    string Shift);

public sealed record MoveWorkerScheduleRequest(
    Guid WorkerId,
    Guid GroupId,
    string Shift);

public sealed record RemoveWorkerScheduleRequest(Guid WorkerId);

public sealed record UpdateWorkerScheduleSettingsRequest(
    string DayStartLocalTime,
    string DayEndLocalTime,
    string NightStartLocalTime,
    string NightEndLocalTime,
    string? TimeZoneId = null);

public sealed record UpdateWorkerScheduleGroupRequest(
    string CurrentWeekShift);

public sealed record WorkerScheduleDistributionResult(
    int AssignedCount,
    IReadOnlyDictionary<Guid, int> GroupCounts);

public sealed record WorkerScheduleConflict(
    string Error,
    Guid WorkerId,
    Guid ExistingGroupId,
    string ExistingGroupName);

public static class WorkerScheduleCalculator
{
    private static readonly string[] Codes = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];
    private static readonly string[] Labels = ["Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота", "Воскресенье"];

    public static string ReferenceOrientation(int dayOff, int currentDay)
    {
        if (dayOff is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(dayOff));
        if (currentDay is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(currentDay));

        var afterDayOff = dayOff % 2 == 0
            ? WorkerScheduleShifts.DayFirst
            : WorkerScheduleShifts.NightFirst;
        return currentDay >= dayOff
            ? afterDayOff
            : WorkerScheduleShifts.Opposite(afterDayOff);
    }

    public static IReadOnlyList<WorkerScheduleDayDto> Calculate(int dayOff, string currentWeekShift, int currentDay)
    {
        if (dayOff is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(dayOff));
        if (currentDay is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(currentDay));
        if (currentWeekShift is not (WorkerScheduleShifts.DayFirst or WorkerScheduleShifts.NightFirst))
            throw new ArgumentException("Unknown starting shift.", nameof(currentWeekShift));

        var result = new List<WorkerScheduleDayDto>(7);
        for (var day = 0; day < 7; day++)
        {
            var distance = (day - dayOff + 7) % 7;
            if (distance == 0)
            {
                result.Add(new(day, Codes[day], Labels[day], WorkerScheduleShifts.Off, WorkerScheduleShifts.Off, true));
                continue;
            }

            var isCurrentCycle = currentDay == dayOff ? day > dayOff : (day > dayOff) == (currentDay > dayOff);
            var orientation = isCurrentCycle ? currentWeekShift : WorkerScheduleShifts.Opposite(currentWeekShift);
            var first = orientation == WorkerScheduleShifts.DayFirst ? WorkerScheduleShifts.Day : WorkerScheduleShifts.Night;
            var second = first == WorkerScheduleShifts.Day ? WorkerScheduleShifts.Night : WorkerScheduleShifts.Day;
            result.Add(new(day, Codes[day], Labels[day], first, second, false));
        }

        return result;
    }

    public static bool IsActiveNow(
        int dayOff,
        string currentWeekShift,
        string shift,
        DateTime localNow,
        string dayStart,
        string dayEnd,
        string nightStart,
        string nightEnd)
    {
        var today = localNow.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)localNow.DayOfWeek - 1;
        if (today == dayOff) return false;
        var dayState = (shift == WorkerScheduleShifts.Shift1) == (currentWeekShift == WorkerScheduleShifts.DayFirst);
        var time = TimeOnly.FromDateTime(localNow);
        return dayState
            ? IsWindowActive(time, dayStart, dayEnd, today, dayOff)
            : IsWindowActive(time, nightStart, nightEnd, today, dayOff);
    }

    private static bool IsWindowActive(TimeOnly time, string fromText, string toText, int today, int dayOff)
    {
        var from = TimeOnly.Parse(fromText);
        var to = TimeOnly.Parse(toText);
        if (from < to) return time >= from && time < to;
        var next = (today + 1) % 7;
        var previous = (today + 6) % 7;
        // The full overnight window is suppressed when it starts before a day off or would
        // continue out of one. This keeps the configured day off inactive for 24 hours.
        return (time >= from && next != dayOff)
            || (time < to && previous != dayOff);
    }
}

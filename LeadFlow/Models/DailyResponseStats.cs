namespace LeadFlow.Models;

/// <summary>Агрегат откликов за один календарный день (локальное время ПК).</summary>
public sealed class DailyResponseBucket
{
    public DateTime DateLocal { get; init; }
    public int Total { get; init; }
    public int Sent { get; init; }
    public int InProgress { get; init; }
    public int ActionRequired { get; init; }
    public int Duplicates { get; init; }
    public int Errors { get; init; }
}

/// <summary>Строка для привязки в UI (столбик и подписи).</summary>
public sealed class DailyResponseStatsRow
{
    public DateTime DateLocal { get; init; }
    public string ShortLabel { get; init; } = string.Empty;
    public string WeekdayLabel { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Sent { get; init; }
    public int InProgress { get; init; }
    public int ActionRequired { get; init; }
    public int Duplicates { get; init; }
    public int Errors { get; init; }
    public double ErrorHeight { get; init; }
    public double DuplicateHeight { get; init; }
    public double ActionRequiredHeight { get; init; }
    public double SentHeight { get; init; }
    public double InProgressHeight { get; init; }
    public string Tooltip { get; init; } = string.Empty;
}

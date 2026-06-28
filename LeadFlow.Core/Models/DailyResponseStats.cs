namespace LeadFlow.Core.Models;

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

/// <summary>Строка для топов по городам/аккаунтам/вакансиям.</summary>
public sealed class HrMetricRow
{
    public string Name { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Sent { get; init; }
    public string ConversionText { get; init; } = "0%";
    public string ShareText { get; init; } = "0%";
}

/// <summary>Распределение по возрастным группам.</summary>
public sealed class AgeBucketMetricRow
{
    public string Bucket { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Sent { get; init; }
    public string ConversionText { get; init; } = "0%";
}

/// <summary>Снимок полезных HR-метрик за период.</summary>
public sealed class HrInsightsSnapshot
{
    public IReadOnlyList<HrMetricRow> TopCities { get; init; } = [];
    public IReadOnlyList<HrMetricRow> TopVacancies { get; init; } = [];
    public IReadOnlyList<HrMetricRow> TopAccounts { get; init; } = [];
    public IReadOnlyList<AgeBucketMetricRow> AgeBuckets { get; init; } = [];
    public string AverageAgeText { get; init; } = "н/д";
    public string MessengerCoverageText { get; init; } = "0%";
}

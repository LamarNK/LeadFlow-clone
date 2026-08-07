using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class DashboardChartsBuilder
{
    private static readonly (string Label, string Color, Func<ActivityPointDto, int> Select)[] ActivityMetrics =
    [
        ("Откликов", "#2563eb", p => p.NewCount),
        ("Отправленные", "#15803d", p => p.SentCount),
        ("Дублей", "#16a34a", p => p.DuplicateCount),
        ("Ошибок", "#f59e0b", p => p.ErrorCount)
    ];

    public static DashboardChartsViewModel FromPresentation(
        IReadOnlyList<DashboardKpiCardViewModel> kpiCards,
        LineChartViewModel activityChart,
        AccountStatsViewModel accountStats)
    {
        var sparklineLabels = ResampleLabels(activityChart.Labels, SparklineGenerator.PointCount);
        var sparklineUtcHours = activityChart.UtcHours.Count > 0
            ? ResampleUtcHours(activityChart.UtcHours, SparklineGenerator.PointCount)
            : [];

        return new DashboardChartsViewModel
        {
            Sparklines = kpiCards
                .Select(k => BuildKpiChart(k, sparklineLabels, sparklineUtcHours, activityChart.ReferenceDayUtc))
                .ToList(),
            HourlyResponses = activityChart,
            AccountStatus = new DonutChartViewModel
            {
                Total = accountStats.Total,
                Active = accountStats.Active,
                Inactive = accountStats.Inactive,
                Blocked = accountStats.Blocked,
                Errors = accountStats.Errors
            }
        };
    }

    private static SparklineChartViewModel BuildKpiChart(
        DashboardKpiCardViewModel card,
        IReadOnlyList<string> sparklineLabels,
        IReadOnlyList<int> sparklineUtcHours,
        string? referenceDayUtc) =>
        card.Segments.Count > 0
            ? new SparklineChartViewModel
            {
                Kind = "segments",
                MetricLabel = TooltipLabelFor(card.Label),
                Segments = card.Segments
            }
            : new SparklineChartViewModel
            {
                Kind = "sparkline",
                Color = card.SparkColor,
                MetricLabel = TooltipLabelFor(card.Label),
                Labels = sparklineLabels,
                Values = card.Sparkline,
                TooltipValues = card.Sparkline,
                UtcHours = sparklineUtcHours,
                ReferenceDayUtc = referenceDayUtc
            };

    internal static IReadOnlyList<KpiChartSegmentViewModel> BuildAccountSegments(AccountStatsViewModel stats)
    {
        var segments = new List<KpiChartSegmentViewModel>
        {
            new() { Label = "Активны", Value = stats.Active, Color = "#22c55e" },
            new() { Label = "Неактивны", Value = stats.Inactive, Color = "#94a3b8" },
            new() { Label = "Ошибки", Value = stats.Errors, Color = "#f59e0b" }
        };

        var withValues = segments.Where(s => s.Value > 0).ToList();
        if (withValues.Count > 0)
            return withValues;

        return [new() { Label = "Нет данных", Value = 1, Color = "#e5e7eb" }];
    }

    internal static IReadOnlyList<KpiChartSegmentViewModel> BuildWorkerSegments(int onlineWorkers, int totalWorkers)
    {
        if (totalWorkers <= 0)
            return [new() { Label = "Нет данных", Value = 1, Color = "#e5e7eb" }];

        var offline = Math.Max(0, totalWorkers - onlineWorkers);
        var segments = new List<KpiChartSegmentViewModel>();
        if (onlineWorkers > 0)
            segments.Add(new() { Label = "Онлайн", Value = onlineWorkers, Color = "#2563eb" });
        if (offline > 0)
            segments.Add(new() { Label = "Офлайн", Value = offline, Color = "#94a3b8" });

        return segments.Count > 0
            ? segments
            : [new() { Label = "Нет данных", Value = 1, Color = "#e5e7eb" }];
    }

    public static LineChartViewModel FromDailyActivity(IReadOnlyList<ActivityPointDto> daily)
    {
        if (daily.Count == 0)
            return new LineChartViewModel();

        var labels = daily
            .Select((p, i) => string.IsNullOrWhiteSpace(p.Label) ? $"День {i + 1}" : p.Label)
            .ToList();
        var series = BuildActivitySeries(daily);

        return new LineChartViewModel
        {
            Labels = labels,
            Values = series[0].Values,
            Series = series
        };
    }

    public static LineChartViewModel FromHourlyActivity(IReadOnlyList<ActivityPointDto> hourly)
    {
        if (hourly.Count == 0)
            hourly = BuildEmptyHourlyActivity();

        var labels = hourly
            .Select((p, i) => string.IsNullOrWhiteSpace(p.Label) ? $"{i:00}:00" : p.Label)
            .ToList();
        var utcHours = hourly
            .Select((p, i) => p.SlotStartHour is >= 0 and <= 23 ? p.SlotStartHour : i)
            .ToList();
        var series = BuildActivitySeries(hourly);

        if (labels.Count < 25 && labels.Count >= 2)
        {
            labels.Add("24:00");
            utcHours.Add(24);
            series = series
                .Select(s => new LineChartSeriesViewModel
                {
                    Label = s.Label,
                    Color = s.Color,
                    Values = s.Values.Concat([s.Values[^1]]).ToList()
                })
                .ToList();
        }

        return new LineChartViewModel
        {
            Labels = labels,
            Values = series[0].Values,
            Series = series,
            UtcHours = utcHours,
            ReferenceDayUtc = DateTime.UtcNow.ToString("yyyy-MM-dd")
        };
    }

    public static IReadOnlyList<DashboardChartPointViewModel> ToResponsePoints(LineChartViewModel chart) =>
        chart.Labels
            .Select((label, i) => new DashboardChartPointViewModel
            {
                Label = label,
                Value = chart.Values.Count > i ? chart.Values[i] : 0,
                ShowAxisLabel = i % 4 == 0 || i == chart.Labels.Count - 1,
                UtcHour = chart.UtcHours.Count > i ? chart.UtcHours[i] : i
            })
            .ToList();

    private static IReadOnlyList<ActivityPointDto> BuildEmptyHourlyActivity() =>
        Enumerable.Range(0, 24)
            .Select(h => new ActivityPointDto($"{h:00}:00", 0, 0, 0, 0, h, 1, null))
            .ToList();

    private static IReadOnlyList<LineChartSeriesViewModel> BuildActivitySeries(IReadOnlyList<ActivityPointDto> points) =>
        ActivityMetrics
            .Select(metric => new LineChartSeriesViewModel
            {
                Label = metric.Label,
                Color = metric.Color,
                Values = points.Select(p => Math.Max(0, metric.Select(p))).ToList()
            })
            .ToList();

    private static IReadOnlyList<int> ResampleUtcHours(IReadOnlyList<int> source, int count)
    {
        if (source.Count == 0)
        {
            return Enumerable.Range(0, count)
                .Select(i => i * 24 / Math.Max(1, count - 1))
                .ToList();
        }

        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            var pos = i * (source.Count - 1) / (double)Math.Max(1, count - 1);
            var idx = (int)Math.Round(pos);
            result[i] = source[Math.Min(idx, source.Count - 1)];
        }

        return result;
    }

    private static IReadOnlyList<string> ResampleLabels(IReadOnlyList<string> source, int count)
    {
        if (source.Count == 0)
        {
            return Enumerable.Range(0, count)
                .Select(i => (i * 24 / Math.Max(1, count - 1)).ToString("00") + ":00")
                .ToList();
        }

        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var pos = i * (source.Count - 1) / (double)Math.Max(1, count - 1);
            var idx = (int)Math.Round(pos);
            result[i] = source[Math.Min(idx, source.Count - 1)];
        }

        return result;
    }

    private static string TooltipLabelFor(string kpiLabel) => kpiLabel switch
    {
        "Откликов всего" => "Откликов",
        "Отправленные" => "Отправленные",
        "Дублей" => "Дублей",
        "Ошибок" => "Ошибок",
        "Аккаунтов активно" => "Аккаунтов",
        "Воркеров онлайн" => "Воркеров",
        _ => kpiLabel
    };
}
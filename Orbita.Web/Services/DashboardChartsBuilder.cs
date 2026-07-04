using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class DashboardChartsBuilder
{
    public static DashboardChartsViewModel FromPresentation(
        IReadOnlyList<DashboardKpiCardViewModel> kpiCards,
        IReadOnlyList<DashboardChartPointViewModel> hourlyChart,
        AccountStatsViewModel accountStats,
        IReadOnlyList<ActivityPointDto>? dailyPoints = null,
        bool useHourlyLabels = false)
    {
        var useHourly = useHourlyLabels || dailyPoints is not { Count: > 0 };
        var labelSource = useHourly
            ? hourlyChart.Select(p => p.Label).ToList()
            : dailyPoints!.Select(p => p.Label).ToList();
        var utcHourSource = useHourly
            ? hourlyChart.Select(p => p.UtcHour).ToList()
            : [];
        var referenceDayUtc = useHourly ? DateTime.UtcNow.ToString("yyyy-MM-dd") : null;
        var sparklineLabels = ResampleLabels(labelSource, SparklineGenerator.PointCount);
        var sparklineUtcHours = utcHourSource.Count > 0
            ? ResampleUtcHours(utcHourSource, SparklineGenerator.PointCount)
            : [];

        return new DashboardChartsViewModel
        {
            Sparklines = kpiCards
                .Select(k => BuildKpiChart(k, sparklineLabels, sparklineUtcHours, referenceDayUtc))
                .ToList(),
            HourlyResponses = new LineChartViewModel
            {
                Labels = hourlyChart.Select(p => p.Label).ToList(),
                Values = hourlyChart.Select(p => p.Value).ToList(),
                UtcHours = hourlyChart.Select(p => p.UtcHour).ToList(),
                ReferenceDayUtc = referenceDayUtc
            },
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

    public static IReadOnlyList<DashboardChartPointViewModel> FromDailyActivity(
        IReadOnlyList<ActivityPointDto> daily,
        int axisEvery = 1)
    {
        if (daily.Count == 0)
            return [];

        return daily.Select((p, i) => new DashboardChartPointViewModel
        {
            Label = string.IsNullOrWhiteSpace(p.Label) ? $"День {i + 1}" : p.Label,
            Value = Math.Max(0, p.NewCount),
            ShowAxisLabel = i % axisEvery == 0 || i == daily.Count - 1
        }).ToList();
    }

    public static IReadOnlyList<DashboardChartPointViewModel> FromHourlyActivity(
        IReadOnlyList<ActivityPointDto> hourly,
        int axisEvery = 4)
    {
        if (hourly.Count == 0)
            return HourlyResponsesGenerator.BuildEmptyDailyPoints();

        var points = hourly.Select((p, i) => new DashboardChartPointViewModel
        {
            Label = string.IsNullOrWhiteSpace(p.Label) ? $"{i:00}:00" : p.Label,
            Value = Math.Max(0, p.NewCount),
            ShowAxisLabel = i % axisEvery == 0,
            UtcHour = p.SlotStartHour is >= 0 and <= 23 ? p.SlotStartHour : i
        }).ToList();

        if (points.Count < 25 && points.Count >= 2)
        {
            var last = points[^1];
            points.Add(new DashboardChartPointViewModel
            {
                Label = "24:00",
                Value = last.Value,
                ShowAxisLabel = true,
                UtcHour = 24
            });
        }

        return points;
    }

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
        "Дублей" => "Дублей",
        "Ошибок" => "Ошибок",
        "Аккаунтов активно" => "Аккаунтов",
        "Воркеров онлайн" => "Воркеров",
        _ => kpiLabel
    };
}
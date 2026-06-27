using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class DashboardChartsBuilder
{
    public static DashboardChartsViewModel FromPresentation(
        IReadOnlyList<DashboardKpiCardViewModel> kpiCards,
        IReadOnlyList<DashboardChartPointViewModel> hourlyChart,
        AccountStatsViewModel accountStats)
    {
        var sparklineLabels = ResampleLabels(
            hourlyChart.Select(p => p.Label).ToList(),
            SparklineGenerator.PointCount);

        return new DashboardChartsViewModel
        {
            Sparklines = kpiCards.Select(k => new SparklineChartViewModel
            {
                Color = k.SparkColor,
                MetricLabel = TooltipLabelFor(k.Label),
                Labels = sparklineLabels,
                Values = k.Sparkline,
                TooltipValues = BuildTooltipValues(k.Label, (int)Math.Round(k.CountValue), hourlyChart, SparklineGenerator.PointCount)
            }).ToList(),
            HourlyResponses = new LineChartViewModel
            {
                Labels = hourlyChart.Select(p => p.Label).ToList(),
                Values = hourlyChart.Select(p => p.Value).ToList()
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

    public static IReadOnlyList<DashboardChartPointViewModel> FromHourlyActivity(
        IReadOnlyList<ActivityPointDto> hourly,
        int axisEvery = 4)
    {
        if (hourly.Count == 0)
            return HourlyResponsesGenerator.BuildEmptyDailyPoints();

        var points = hourly.Select((p, i) => new DashboardChartPointViewModel
        {
            Label = string.IsNullOrWhiteSpace(p.Label) ? $"{i:00}:00" : p.Label,
            Value = Math.Clamp(p.NewCount, 0, 100),
            ShowAxisLabel = i % axisEvery == 0
        }).ToList();

        if (points.Count < 25 && points.Count >= 2)
        {
            var last = points[^1];
            points.Add(new DashboardChartPointViewModel
            {
                Label = "24:00",
                Value = last.Value,
                ShowAxisLabel = true
            });
        }

        return points;
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

    private static IReadOnlyList<int> BuildTooltipValues(
        string kpiLabel,
        int countValue,
        IReadOnlyList<DashboardChartPointViewModel> hourlyChart,
        int count)
    {
        if (kpiLabel is "Аккаунтов активно" or "Воркеров онлайн")
            return Enumerable.Repeat(countValue, count).ToList();

        var hourlyValues = hourlyChart.Count > 0
            ? hourlyChart.Select(p => p.Value).ToList()
            : [];

        var resampled = ResampleValues(hourlyValues, count);
        if (kpiLabel == "Откликов всего")
            return resampled;

        var sum = resampled.Sum();
        if (sum <= 0)
            return resampled;

        return resampled
            .Select(v => Math.Max(0, (int)Math.Round(v * countValue / (double)sum)))
            .ToList();
    }

    private static IReadOnlyList<int> ResampleValues(IReadOnlyList<int> source, int count)
    {
        if (source.Count == 0)
            return Enumerable.Repeat(0, count).ToList();

        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            var pos = i * (source.Count - 1) / (double)Math.Max(1, count - 1);
            var idx = (int)Math.Floor(pos);
            var frac = pos - idx;
            var a = source[Math.Min(idx, source.Count - 1)];
            var b = source[Math.Min(idx + 1, source.Count - 1)];
            result[i] = (int)Math.Round(a + (b - a) * frac);
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
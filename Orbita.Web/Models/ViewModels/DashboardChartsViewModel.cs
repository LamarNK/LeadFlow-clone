namespace Orbita.Web.Models.ViewModels;

public sealed class DashboardChartsViewModel
{
    public IReadOnlyList<SparklineChartViewModel> Sparklines { get; init; } = [];
    public LineChartViewModel HourlyResponses { get; init; } = new();
    public DonutChartViewModel AccountStatus { get; init; } = new();
}

public sealed class SparklineChartViewModel
{
    public string Kind { get; init; } = "sparkline";
    public string Color { get; init; } = "#2563eb";
    public string MetricLabel { get; init; } = string.Empty;
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<int> Values { get; init; } = [];
    public IReadOnlyList<int> TooltipValues { get; init; } = [];
    public IReadOnlyList<int> UtcHours { get; init; } = [];
    public string? ReferenceDayUtc { get; init; }
    public IReadOnlyList<KpiChartSegmentViewModel> Segments { get; init; } = [];
}

public sealed class KpiChartSegmentViewModel
{
    public string Label { get; init; } = string.Empty;
    public int Value { get; init; }
    public string Color { get; init; } = "#94a3b8";
}

public sealed class LineChartViewModel
{
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<int> Values { get; init; } = [];
    public IReadOnlyList<int> UtcHours { get; init; } = [];
    public string? ReferenceDayUtc { get; init; }
    public bool HasData => Values.Count > 0 && Values.Any(v => v > 0);
}

public sealed class DonutChartViewModel
{
    public int Total { get; init; }
    public int Active { get; init; }
    public int Inactive { get; init; }
    public int Blocked { get; init; }
    public int Errors { get; init; }
}
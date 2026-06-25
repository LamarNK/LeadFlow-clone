namespace Orbita.Web.Models.ViewModels;

public sealed class DashboardChartsViewModel
{
    public IReadOnlyList<SparklineChartViewModel> Sparklines { get; init; } = [];
    public LineChartViewModel HourlyResponses { get; init; } = new();
    public DonutChartViewModel AccountStatus { get; init; } = new();
}

public sealed class SparklineChartViewModel
{
    public string Color { get; init; } = "#2563eb";
    public string MetricLabel { get; init; } = string.Empty;
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<int> Values { get; init; } = [];
    public IReadOnlyList<int> TooltipValues { get; init; } = [];
}

public sealed class LineChartViewModel
{
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<int> Values { get; init; } = [];
}

public sealed class DonutChartViewModel
{
    public int Total { get; init; }
    public int Active { get; init; }
    public int Inactive { get; init; }
    public int Blocked { get; init; }
    public int Errors { get; init; }
}
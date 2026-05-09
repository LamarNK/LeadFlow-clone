namespace LeadFlow.ViewModels;

public enum DashboardChartSeries
{
    Responses,
    Crm,
    Duplicates,
    Errors
}

public sealed record ChartSeriesTab(DashboardChartSeries Series, string Title);

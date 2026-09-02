namespace Orbita.Web.Models.ViewModels;

public sealed class WorkerBrowserMonitorViewModel
{
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsMonitoringPaused { get; init; }
    public IReadOnlyList<BreadcrumbItemViewModel> Breadcrumbs { get; init; } = [];
}
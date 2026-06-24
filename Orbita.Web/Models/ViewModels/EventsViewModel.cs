using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class EventsIndexViewModel
{
    public PageHeaderViewModel Header { get; init; } = new() { Title = "События", Subtitle = "Последние события по всем воркерам", ShowRefresh = true };
    public IReadOnlyList<WorkerEventListItem> Events { get; init; } = [];
}
using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class WorkersIndexViewModel
{
    public PageHeaderViewModel Header { get; init; } = new() { Title = "Воркеры", Subtitle = "Состояние экземпляров на VDS", ShowRefresh = true };
    public IReadOnlyList<WorkerListItem> Workers { get; init; } = [];
}

public sealed class WorkerDetailsViewModel
{
    public PageHeaderViewModel Header { get; init; } = new();
    public WorkerDetail? Worker { get; init; }
    public IReadOnlyList<WorkerAccountDto> Accounts { get; init; } = [];
}
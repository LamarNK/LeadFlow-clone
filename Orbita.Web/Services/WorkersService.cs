using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public sealed class WorkersService(OrbitaApiClient api) : IWorkersService
{
    public async Task<WorkersIndexViewModel> GetIndexAsync(CancellationToken ct = default)
    {
        return new WorkersIndexViewModel
        {
            Header = new PageHeaderViewModel
            {
                Title = "Воркеры",
                Subtitle = "Состояние экземпляров на VDS",
                ShowRefresh = true,
                UpdatedAt = DateTime.Now
            },
            Workers = await api.GetWorkersAsync(ct) ?? []
        };
    }

    public async Task<WorkerDetailsViewModel?> GetDetailsAsync(Guid id, CancellationToken ct = default)
    {
        var worker = await api.GetWorkerAsync(id, ct);
        if (worker is null) return null;

        return new WorkerDetailsViewModel
        {
            Header = new PageHeaderViewModel
            {
                Title = worker.DisplayName,
                Subtitle = $"{worker.MachineName} · {worker.AppVersion}"
            },
            Worker = worker,
            Accounts = await api.GetWorkerAccountsAsync(id, ct) ?? []
        };
    }
}
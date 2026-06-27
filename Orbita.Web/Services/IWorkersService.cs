using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IWorkersService
{
    Task<WorkersIndexViewModel> GetIndexAsync(string? searchQuery = null, int page = 1, CancellationToken ct = default);
    Task<WorkerDetailsViewModel?> GetDetailsAsync(Guid id, CancellationToken ct = default);
    Task<(CreateWorkerResultViewModel? Result, string? Error)> CreateWorkerAsync(
        string displayName,
        Guid? officeId = null,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateWorkerSettingsAsync(Guid workerId, int maxConcurrentAccounts, CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateWorkerAccountAsync(Guid workerId, Guid accountId, bool isEnabled, CancellationToken ct = default);

    Task<(Stream? Stream, string? FileName, string? Error)> OpenLatestWorkerReleaseDownloadAsync(CancellationToken ct = default);
}
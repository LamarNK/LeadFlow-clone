using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IWorkersService
{
    Task<WorkersIndexViewModel> GetIndexAsync(
        string? searchQuery = null,
        string? status = null,
        int page = 1,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default);
    Task<WorkerDetailsViewModel?> GetDetailsAsync(
        Guid id,
        string? logsQ = null,
        string? logsLevel = null,
        DateTime? logsDate = null,
        int logsPage = 1,
        bool includeLogs = false,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default);
    Task<(CreateWorkerResultViewModel? Result, string? Error)> CreateWorkerAsync(
        string displayName,
        Guid? officeId = null,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateWorkerSettingsAsync(
        Guid workerId,
        int maxConcurrentAccounts,
        string? adsPowerApiBaseUrl,
        string? adsPowerApiKey,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateWorkerAccountAsync(Guid workerId, Guid accountId, bool isEnabled, CancellationToken ct = default);
    Task<(bool Success, string? Error)> RequestSubProfilesRefreshAsync(Guid workerId, Guid accountId, CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateSubProfileEnabledAsync(
        Guid workerId,
        Guid accountId,
        string subProfileId,
        bool isEnabledInPanel,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> SendWorkerCommandAsync(Guid workerId, string command, CancellationToken ct = default);
    Task<(bool Success, string? Error)> SetWorkerEnabledAsync(Guid workerId, bool enabled, CancellationToken ct = default);
    Task<(bool Success, string? Error)> DeleteWorkerAsync(Guid workerId, CancellationToken ct = default);
    Task<(string? ApiKey, string? Error)> RotateWorkerApiKeyAsync(Guid workerId, CancellationToken ct = default);

    Task<(Stream? Stream, string? FileName, string? Error)> OpenLatestWorkerReleaseDownloadAsync(CancellationToken ct = default);
}
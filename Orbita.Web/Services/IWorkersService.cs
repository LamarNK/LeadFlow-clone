using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IWorkersService
{
    Task<WorkersIndexViewModel> GetIndexAsync(
        string? searchQuery = null,
        string? status = null,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default);
    Task<WorkerDetailsViewModel?> GetDetailsAsync(
        Guid id,
        string? sort = null,
        string? sortDir = null,
        string? accountSearchQuery = null,
        string? accountGroupId = null,
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
        string? adsPowerGroupId = null,
        bool responseFilterEnabled = false,
        bool responseFilterExcludeFemale = false,
        bool responseFilterExcludeMale = false,
        int? responseFilterMaxAgeMale = null,
        int? responseFilterMaxAgeFemale = null,
        int? responseFilterMaxAgeDays = null,
        bool responseHighlightEnabled = false,
        string? responseHighlightAgeBuckets = null,
        bool autoScheduleEnabled = false,
        string? autoScheduleDays = null,
        string? autoScheduleFromLocalTime = null,
        string? autoScheduleToLocalTime = null,
        bool messengerAutoReplyEnabled = false,
        string? messengerAutoReplyMessage = null,
        int? phoneUnchangedHours = null,
        bool? autoDeliverToCrm = null,
        bool? autoDeliverToBitrix = null,
        string? responseHighlightTargetsJson = null,
        string? ruCaptchaApiKey = null,
        string? multiloginLauncherUrl = null,
        string? multiloginCloudApiUrl = null,
        string? multiloginAutomationToken = null,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateWorkerAccountAsync(Guid workerId, Guid accountId, bool isEnabled, CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateWorkerAccountCredentialsAsync(
        Guid workerId,
        Guid accountId,
        string? login,
        string? password,
        bool clear,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> RequestSubProfilesRefreshAsync(Guid workerId, Guid accountId, CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateSubProfileEnabledAsync(
        Guid workerId,
        Guid accountId,
        string subProfileId,
        bool isEnabledInPanel,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> SendWorkerCommandAsync(Guid workerId, string command, CancellationToken ct = default);
    Task<(bool Success, string? Error)> SetWorkerEnabledAsync(Guid workerId, bool enabled, CancellationToken ct = default);
    Task<(BulkWorkersMonitoringResultDto? Result, string? Error)> SetAllWorkersMonitoringAsync(
        bool enabled,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> DeleteWorkerAsync(Guid workerId, CancellationToken ct = default);
    Task<(string? ApiKey, string? Error)> RotateWorkerApiKeyAsync(Guid workerId, CancellationToken ct = default);

    Task<(Stream? Stream, string? FileName, string? Error)> OpenLatestWorkerReleaseDownloadAsync(CancellationToken ct = default);
}

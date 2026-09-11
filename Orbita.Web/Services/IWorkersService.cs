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
        string? accountProvider = null,
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
        string? localChromeExecutablePath = null,
        bool adsPowerEnabled = true,
        bool multiloginEnabled = true,
        bool localChromeEnabled = true,
        CancellationToken ct = default);
    Task<IReadOnlyList<WorkerSettingsTemplateDto>> GetWorkerSettingsTemplatesAsync(
        Guid workerId,
        CancellationToken ct = default);
    Task<(WorkerSettingsTemplateDto? Template, IReadOnlyList<WorkerSettingsTemplateDto> Templates, string? Error)> CreateWorkerSettingsTemplateAsync(
        Guid workerId,
        string name,
        WorkerSettingsTemplatePayload settings,
        CancellationToken ct = default);
    Task<(WorkerSettingsTemplateDto? Template, IReadOnlyList<WorkerSettingsTemplateDto> Templates, string? Error)> UpdateWorkerSettingsTemplateAsync(
        Guid workerId,
        Guid templateId,
        string name,
        WorkerSettingsTemplatePayload settings,
        CancellationToken ct = default);
    Task<(IReadOnlyList<WorkerSettingsTemplateDto> Templates, string? Error)> DeleteWorkerSettingsTemplateAsync(
        Guid workerId,
        Guid templateId,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateWorkerAccountAsync(Guid workerId, Guid accountId, bool isEnabled, CancellationToken ct = default);
    Task<(bool Success, string? Error)> CreateLocalAccountAsync(
        Guid workerId,
        string displayName,
        string? localUserDataDir = null,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateLocalAccountAsync(
        Guid workerId,
        Guid accountId,
        string? displayName,
        string? localUserDataDir,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> DeleteLocalAccountAsync(Guid workerId, Guid accountId, CancellationToken ct = default);
    Task<(bool Success, string? Error)> OpenLocalBrowserAsync(Guid workerId, Guid accountId, CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateWorkerAccountCredentialsAsync(
        Guid workerId,
        Guid accountId,
        string? login,
        string? password,
        bool clear,
        CancellationToken ct = default);
    Task<(LocalWorkerAccountProfileDto? Profile, string? Error)> UpdateLocalAccountProfileAsync(
        Guid workerId,
        Guid accountId,
        string? login,
        string? password,
        bool clearCredentials,
        bool? proxyEnabled,
        string? proxyAddress,
        string? proxyUsername,
        string? proxyPassword,
        bool clearProxyPassword,
        string? trafficMode = null,
        bool? blockMedia = null,
        bool? blockAnalytics = null,
        bool? blockImages = null,
        bool? blockFonts = null,
        bool? blockPrefetch = null,
        int? navigationTimeoutSeconds = null,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> RequestSubProfilesRefreshAsync(Guid workerId, Guid accountId, CancellationToken ct = default);
    Task<(bool Success, string? Error)> RequestProviderCheckAsync(Guid workerId, string provider, CancellationToken ct = default);
    Task<(bool Success, string? Error)> RequestProviderSyncAsync(Guid workerId, string provider, CancellationToken ct = default);
    Task<(bool Success, string? Error)> UpdateSubProfileEnabledAsync(
        Guid workerId,
        Guid accountId,
        string subProfileId,
        bool isEnabledInPanel,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> SendWorkerCommandAsync(Guid workerId, string command, CancellationToken ct = default);
    Task<(bool Success, string? Error)> RenameWorkerAsync(Guid workerId, string displayName, CancellationToken ct = default);
    Task<(bool Success, string? Error)> SetWorkerEnabledAsync(Guid workerId, bool enabled, CancellationToken ct = default);
    Task<(BulkWorkersMonitoringResultDto? Result, string? Error)> SetAllWorkersMonitoringAsync(
        bool enabled,
        CancellationToken ct = default);
    Task<(bool Success, string? Error)> DeleteWorkerAsync(Guid workerId, CancellationToken ct = default);
    Task<(string? ApiKey, string? Error)> RotateWorkerApiKeyAsync(Guid workerId, CancellationToken ct = default);

    Task<(Stream? Stream, string? FileName, string? Error)> OpenLatestWorkerReleaseDownloadAsync(CancellationToken ct = default);

    Task<(TopUpSessionViewModel? Session, string? Error, Guid? ConflictSessionId)> CreateTopUpSessionAsync(
        Guid workerId,
        Guid accountId,
        string subProfileId,
        CancellationToken ct = default);
    Task<TopUpSessionViewModel?> GetTopUpSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<(bool Success, string? Error)> CancelTopUpSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<(bool Success, string? Error)> MarkTopUpSessionPaidAsync(Guid sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<TopUpSessionViewModel>> GetTopUpSessionsAsync(bool history, CancellationToken ct = default);
}

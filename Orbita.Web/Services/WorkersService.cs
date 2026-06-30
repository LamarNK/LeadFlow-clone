using System.Security.Claims;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class WorkersService(
    OrbitaApiClient api,
    IHttpContextAccessor httpContextAccessor,
    IOptions<DesignPreviewOptions> previewOptions) : IWorkersService
{
    private const int DefaultPageSize = 12;

    public async Task<WorkersIndexViewModel> GetIndexAsync(
        string? searchQuery = null,
        string? status = null,
        int page = 1,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        status = NormalizeStatusFilter(status);

        if (previewOptions.Value.Enabled)
            return DesignPreviewData.BuildWorkersIndexViewModel(searchQuery, status, page, DefaultPageSize);

        var workers = await api.GetWorkersAsync(ct) ?? [];
        var latestRelease = await api.GetLatestWorkerReleaseAsync(ct);
        var isAdmin = httpContextAccessor.HttpContext?.User.IsInRole(PanelRoles.Admin) == true;
        var offices = isAdmin ? await api.GetOfficesAsync(ct) ?? [] : [];
        var rows = workers.Select(MapRow).ToList();

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var q = searchQuery.Trim();
            rows = rows
                .Where(w => w.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        rows = FilterByStatus(rows, status);

        return BuildIndexViewModel(
            rows,
            searchQuery,
            status,
            page,
            DefaultPageSize,
            latestRelease,
            isAdmin,
            offices);
    }

    private static string? NormalizeStatusFilter(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "online" => "online",
            "offline" => "offline",
            _ => null
        };

    private static List<WorkerRowViewModel> FilterByStatus(IReadOnlyList<WorkerRowViewModel> rows, string? status) =>
        status switch
        {
            "online" => rows.Where(w => w.IsOnline).ToList(),
            "offline" => rows.Where(w => !w.IsOnline).ToList(),
            _ => rows.ToList()
        };

    public async Task<WorkerDetailsViewModel?> GetDetailsAsync(
        Guid id,
        string? logsQ = null,
        string? logsLevel = null,
        DateTime? logsDate = null,
        int logsPage = 1,
        bool includeLogs = false,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            if (!includeLogs)
            {
                return DesignPreviewData.BuildWorkerDetailsViewModel(id);
            }

            return DesignPreviewData.BuildWorkerDetailsViewModelWithLogs(
                id,
                logsQ,
                logsLevel,
                logsDate,
                logsPage);
        }

        var apiWorker = await api.GetWorkerAsync(id, ct);
        if (apiWorker is null) return null;

        var accounts = await api.GetWorkerAccountsAsync(id, ct) ?? [];
        var events = await api.GetEventsAsync(workerId: id, limit: 10, ct: ct) ?? [];
        var workerEvents = events
            .Where(e => e.WorkerId == id)
            .Take(10)
            .Select(e => new DashboardEventRowViewModel
            {
                Message = e.Message,
                Subtitle = !string.IsNullOrWhiteSpace(e.Details)
                    ? e.Details
                    : e.AccountId.HasValue ? "Аккаунт" : string.Empty,
                TimeUtc = e.CreatedAtUtc,
                Level = e.Level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? "error"
                    : e.Level.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? "warning" : "success"
            })
            .ToList();

        var accountRows = accounts
            .Select(a =>
            {
                var balance = apiWorker.Balances.FirstOrDefault(b => b.AccountId == a.AccountId);
                return WorkerDetailsBuilder.MapAccount(a, balance);
            })
            .ToList();

        WorkerLogsPanelViewModel? logsPanel = null;
        if (includeLogs)
        {
            logsPage = Math.Max(1, logsPage);
            var logsPageDto = await api.GetWorkerLogsAsync(
                id,
                logsQ,
                logsLevel,
                logsDate ?? DateTime.UtcNow.Date,
                logsPage,
                SettingsIndexBuilder.LogsPageSize,
                ct) ?? new WorkerLogsPageDto([], 0, logsPage, SettingsIndexBuilder.LogsPageSize);

            logsPanel = SettingsIndexBuilder.BuildWorkerDetailsLogsPanel(
                logsQ,
                logsLevel,
                logsDate,
                logsPage,
                logsPageDto);
        }

        return WorkerDetailsBuilder.Build(
            apiWorker,
            accountRows,
            workerEvents,
            new WorkerExtraInfoViewModel
            {
                IpAddress = string.IsNullOrWhiteSpace(apiWorker.IpAddress) ? "—" : apiWorker.IpAddress,
                StartedAtUtc = apiWorker.StartedAtUtc,
                LeadFlowVersion = apiWorker.AppVersion,
                AgentVersion = string.IsNullOrWhiteSpace(apiWorker.AgentVersion)
                    ? apiWorker.AppVersion
                    : apiWorker.AgentVersion,
                OperatingSystem = string.IsNullOrWhiteSpace(apiWorker.OperatingSystem) ? "—" : apiWorker.OperatingSystem,
                ConnectionCheck = apiWorker.IsOnline ? "Успешно" : "Нет связи"
            },
            logs: logsPanel);
    }

    public async Task<(CreateWorkerResultViewModel? Result, string? Error)> CreateWorkerAsync(
        string displayName,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (new CreateWorkerResultViewModel
            {
                WorkerId = Guid.NewGuid(),
                DisplayName = displayName,
                ApiKey = "preview-api-key",
                InstallCommand = "Запустите Orbita.Worker.Setup-Release.msi, затем вставьте API-ключ в мастере настройки."
            }, null);
        }

        var (result, error) = await api.CreateWorkerAsync(displayName, officeId, ct);
        if (error is not null || result is null)
        {
            return (null, error ?? "Не удалось создать воркер.");
        }

        var latestRelease = await api.GetLatestWorkerReleaseAsync(ct);
        var msiName = latestRelease?.DownloadFileName ?? "Orbita.Worker.Setup.msi";
        return (new CreateWorkerResultViewModel
        {
            WorkerId = result.WorkerId,
            DisplayName = result.DisplayName,
            ApiKey = result.ApiKey,
            InstallCommand = $"Скачайте и установите {msiName} на VDS, затем в мастере настройки вставьте API-ключ: {result.ApiKey}"
        }, null);
    }

    public Task<(bool Success, string? Error)> UpdateWorkerSettingsAsync(
        Guid workerId,
        int maxConcurrentAccounts,
        string? adsPowerApiBaseUrl,
        string? adsPowerApiKey,
        CancellationToken ct = default) =>
        api.UpdateWorkerSettingsAsync(workerId, maxConcurrentAccounts, adsPowerApiBaseUrl, adsPowerApiKey, ct);

    public Task<(bool Success, string? Error)> UpdateWorkerAccountAsync(
        Guid workerId,
        Guid accountId,
        bool isEnabled,
        CancellationToken ct = default) =>
        api.UpdateWorkerAccountAsync(workerId, accountId, isEnabled, ct);

    public async Task<(bool Success, string? Error)> SendWorkerCommandAsync(
        Guid workerId,
        string command,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        return await api.SendWorkerCommandAsync(workerId, command, ct);
    }

    public Task<(Stream? Stream, string? FileName, string? Error)> OpenLatestWorkerReleaseDownloadAsync(
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(Stream?, string?, string?)>((null, null, "Режим предпросмотра."))
            : api.OpenLatestWorkerReleaseDownloadAsync(ct);

    private static WorkersIndexViewModel BuildIndexViewModel(
        IReadOnlyList<WorkerRowViewModel> allRows,
        string? searchQuery,
        string? statusFilter,
        int page,
        int pageSize,
        WorkerReleaseLatestDto? latestRelease = null,
        bool canSelectOffice = false,
        IReadOnlyList<OfficeDto>? offices = null)
    {
        var total = allRows.Count;
        var paged = allRows
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var online = allRows.Count(w => w.IsOnline);
        var offline = allRows.Count - online;
        var totalResponses = allRows.Sum(w => w.Responses);
        var totalErrors = allRows.Sum(w => w.Errors);

        return new WorkersIndexViewModel
        {
            Header = PageHeaderBuilder.WorkersList(),
            SearchQuery = searchQuery,
            StatusFilter = statusFilter,
            KpiCards =
            [
                new()
                {
                    Label = "Всего воркеров",
                    Value = total.ToString(),
                    CountValue = total,
                    IconClass = "fa-solid fa-server",
                    IconTone = "blue"
                },
                new()
                {
                    Label = "Онлайн",
                    Value = online.ToString(),
                    CountValue = online,
                    IconClass = "fa-solid fa-circle-check",
                    IconTone = "green"
                },
                new()
                {
                    Label = "Оффлайн",
                    Value = offline.ToString(),
                    CountValue = offline,
                    IconClass = "fa-solid fa-circle-xmark",
                    IconTone = "orange"
                },
                new()
                {
                    Label = "Всего откликов",
                    Value = totalResponses.ToString(),
                    CountValue = totalResponses,
                    IconClass = "fa-regular fa-comments",
                    IconTone = "blue"
                },
                new()
                {
                    Label = "Ошибок",
                    Value = totalErrors.ToString(),
                    CountValue = totalErrors,
                    IconClass = "fa-solid fa-triangle-exclamation",
                    IconTone = "orange"
                }
            ],
            Workers = paged,
            Pagination = new PaginationViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalItems = total
            },
            HasWorkerRelease = latestRelease is not null,
            LatestWorkerReleaseVersion = latestRelease?.Version,
            LatestWorkerDownloadUrl = latestRelease is null ? null : "/Workers/DownloadLatest",
            CanSelectOffice = canSelectOffice,
            CanCreateWorker = true,
            OfficeOptions = (offices ?? [])
                .Select(o => new EventFilterOptionViewModel { Value = o.Id.ToString(), Label = o.Name })
                .ToList()
        };
    }

    private static WorkerRowViewModel MapRow(WorkerListItem w) => new()
    {
        Id = w.Id,
        DisplayName = w.DisplayName,
        IsOnline = w.IsOnline,
        ActiveAccounts = w.AccountCount,
        TotalAccounts = w.AccountCount,
        Responses = w.TotalToday,
        Duplicates = 0,
        Errors = w.Errors,
        LastActivityUtc = w.LastSeenAtUtc,
        UpdateAvailable = w.UpdateAvailable,
        LatestReleaseVersion = w.LatestReleaseVersion,
        OfficeName = w.OfficeName,
        IsEnabled = w.IsEnabled
    };

    public Task<(bool Success, string? Error)> SetWorkerEnabledAsync(
        Guid workerId,
        bool enabled,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.SetWorkerEnabledAsync(workerId, enabled, ct);

    public Task<(bool Success, string? Error)> DeleteWorkerAsync(
        Guid workerId,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.DeleteWorkerAsync(workerId, ct);

    public async Task<(string? ApiKey, string? Error)> RotateWorkerApiKeyAsync(
        Guid workerId,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return ("preview-rotated-key", null);
        }

        var (result, error) = await api.RotateWorkerKeyAsync(workerId, ct);
        return result is null ? (null, error ?? "Не удалось перевыпустить API-ключ.") : (result.ApiKey, null);
    }
}
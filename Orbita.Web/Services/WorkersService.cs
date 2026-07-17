using System.Security.Claims;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class WorkersService(
    OrbitaApiClient api,
    IOfficeContext officeContext,
    IHttpContextAccessor httpContextAccessor,
    IOptions<DesignPreviewOptions> previewOptions) : IWorkersService
{
    public async Task<WorkersIndexViewModel> GetIndexAsync(
        string? searchQuery = null,
        string? status = null,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = ListPageSizeDefaults.Normalize(pageSize, ListPageSizeDefaults.Workers);
        status = NormalizeStatusFilter(status);

        if (previewOptions.Value.Enabled)
            return DesignPreviewData.BuildWorkersIndexViewModel(
                searchQuery,
                status,
                page,
                pageSize.Value,
                sort,
                sortDir,
                officeContext.EffectiveOfficeId,
                officeContext.ShowOfficeColumn);

        var workers = await api.GetWorkersAsync(ct) ?? [];
        var latestRelease = await api.GetLatestWorkerReleaseAsync(ct);
        var isAdmin = httpContextAccessor.HttpContext?.User.IsInRole(PanelRoles.Admin) == true;
        var offices = isAdmin ? await api.GetOfficesAsync(ct) ?? [] : [];
        var rows = workers.Select(MapRow).ToList();

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            rows = rows
                .Where(w => SearchQueryNormalizer.MatchesTokens(searchQuery, w.DisplayName, w.MachineName))
                .ToList();
        }

        rows = FilterByStatus(rows, status);

        return BuildIndexViewModel(
            rows,
            searchQuery,
            status,
            page,
            pageSize.Value,
            sort,
            sortDir,
            latestRelease,
            isAdmin,
            offices,
            officeContext.ShowOfficeColumn);
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
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default)
    {
        var tableSort = TableSort.Parse(sort, sortDir, TableSort.WorkerAccounts.Default, TableSort.WorkerAccounts.Columns);

        if (previewOptions.Value.Enabled)
        {
            if (!includeLogs)
            {
                return DesignPreviewData.BuildWorkerDetailsViewModel(id, sort, sortDir);
            }

            return DesignPreviewData.BuildWorkerDetailsViewModelWithLogs(
                id,
                logsQ,
                logsLevel,
                logsDate,
                logsPage,
                sort,
                sortDir);
        }

        var apiWorker = await api.GetWorkerAsync(id, ct);
        if (apiWorker is null) return null;

        var accounts = await api.GetWorkerAccountsAsync(id, ct) ?? [];
        var events = await api.GetEventsAsync(workerId: id, limit: 10, ct: ct) ?? [];
        var workerEvents = events
            .Where(e => e.WorkerId == id)
            .Take(10)
            .Select(DashboardEventMapper.Map)
            .ToList();

        var activeAccounts = apiWorker.ActiveAccounts ?? apiWorker.CurrentActivity?.ActiveAccounts;
        var accountRows = TableSort.WorkerAccounts.Apply(
            accounts.Select(a =>
            {
                var balance = apiWorker.Balances.FirstOrDefault(b => b.AccountId == a.AccountId);
                return WorkerDetailsBuilder.MapAccount(a, balance, id, activeAccounts, apiWorker.IsOnline);
            }),
            tableSort).ToList();

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
            logs: logsPanel,
            sort: tableSort);
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

        officeId ??= officeContext.EffectiveOfficeId;
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
        bool responseFilterEnabled = false,
        bool responseFilterExcludeFemale = false,
        int? responseFilterMaxAge = null,
        CancellationToken ct = default) =>
        api.UpdateWorkerSettingsAsync(
            workerId,
            maxConcurrentAccounts,
            adsPowerApiBaseUrl,
            adsPowerApiKey,
            responseFilterEnabled,
            responseFilterExcludeFemale,
            responseFilterMaxAge,
            ct);

    public Task<(bool Success, string? Error)> UpdateWorkerAccountAsync(
        Guid workerId,
        Guid accountId,
        bool isEnabled,
        CancellationToken ct = default) =>
        api.UpdateWorkerAccountAsync(workerId, accountId, isEnabled, ct);

    public Task<(bool Success, string? Error)> RequestSubProfilesRefreshAsync(
        Guid workerId,
        Guid accountId,
        CancellationToken ct = default) =>
        api.RequestSubProfilesRefreshAsync(workerId, accountId, ct);

    public Task<(bool Success, string? Error)> UpdateSubProfileEnabledAsync(
        Guid workerId,
        Guid accountId,
        string subProfileId,
        bool isEnabledInPanel,
        CancellationToken ct = default) =>
        api.UpdateSubProfileEnabledAsync(workerId, accountId, subProfileId, isEnabledInPanel, ct);

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

    private WorkersIndexViewModel BuildIndexViewModel(
        IReadOnlyList<WorkerRowViewModel> allRows,
        string? searchQuery,
        string? statusFilter,
        int page,
        int pageSize,
        string? sort = null,
        string? sortDir = null,
        WorkerReleaseLatestDto? latestRelease = null,
        bool canSelectOffice = false,
        IReadOnlyList<OfficeDto>? offices = null,
        bool showOfficeColumn = false)
    {
        var tableSort = TableSort.Parse(sort, sortDir, TableSort.Workers.Default, TableSort.Workers.Columns);
        var sorted = TableSort.Workers.Apply(allRows, tableSort).ToList();
        var total = sorted.Count;
        var paged = sorted
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var online = allRows.Count(w => w.IsOnline);
        var offline = allRows.Count - online;
        var totalResponses = allRows.Sum(w => w.Responses);
        var totalDuplicates = allRows.Sum(w => w.Duplicates);
        var totalErrors = allRows.Sum(w => w.Errors);

        return new WorkersIndexViewModel
        {
            Header = PageHeaderBuilder.WithOfficeScope(PageHeaderBuilder.WorkersList(), officeContext),
            SearchQuery = searchQuery,
            StatusFilter = statusFilter,
            KpiCards =
            [
                new()
                {
                    Key = "total",
                    Href = KpiCardLinks.WorkersCard("total"),
                    Label = "Всего воркеров",
                    Value = total.ToString(),
                    CountValue = total,
                    IconClass = "fa-solid fa-server",
                    IconTone = "blue"
                },
                new()
                {
                    Key = "online",
                    Href = KpiCardLinks.WorkersCard("online"),
                    Label = "Онлайн",
                    Value = online.ToString(),
                    CountValue = online,
                    IconClass = "fa-solid fa-circle-check",
                    IconTone = "green"
                },
                new()
                {
                    Key = "offline",
                    Href = KpiCardLinks.WorkersCard("offline"),
                    Label = "Оффлайн",
                    Value = offline.ToString(),
                    CountValue = offline,
                    IconClass = "fa-solid fa-circle-xmark",
                    IconTone = "orange"
                },
            new()
            {
                Key = "responses",
                Href = KpiCardLinks.WorkersCard("responses"),
                Label = "Всего откликов",
                Value = totalResponses.ToString(),
                CountValue = totalResponses,
                Delta = "Сегодня",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-comments",
                IconTone = "blue"
            },
            new()
            {
                Key = "duplicates",
                Href = KpiCardLinks.WorkersCard("responses"),
                Label = "Дублей",
                Value = totalDuplicates.ToString(),
                CountValue = totalDuplicates,
                Delta = "Сегодня",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-clone",
                IconTone = "green"
            },
            new()
            {
                Key = "errors",
                    Href = KpiCardLinks.WorkersCard("errors"),
                Label = "Ошибок",
                Value = totalErrors.ToString(),
                CountValue = totalErrors,
                Delta = "Сегодня",
                DeltaTone = totalErrors > 0 ? "bad" : "good",
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
                .ToList(),
            HasActiveFilters = !string.IsNullOrWhiteSpace(searchQuery) || !string.IsNullOrWhiteSpace(statusFilter),
            ActiveFilterChips = FilterChipsBuilder.ForWorkers(searchQuery, statusFilter, pageSize),
            Sort = tableSort,
            ShowOfficeColumn = showOfficeColumn
        };
    }

    private static WorkerRowViewModel MapRow(WorkerListItem w)
    {
        var activity = WorkerActivityPresenter.Present(
            w.CurrentActivity,
            w.IsOnline,
            w.ActiveAccounts ?? w.CurrentActivity?.ActiveAccounts);
        return new WorkerRowViewModel
        {
            Id = w.Id,
            DisplayName = w.DisplayName,
            MachineName = w.MachineName,
            IsOnline = w.IsOnline,
            ActiveAccounts = w.ActiveAccountCount,
            TotalAccounts = w.AccountCount,
            Responses = w.TotalToday,
            Duplicates = w.DuplicatesToday,
            Errors = w.Errors,
            LastActivityUtc = w.LastSeenAtUtc,
            UpdateAvailable = w.UpdateAvailable,
            LatestReleaseVersion = w.LatestReleaseVersion,
            OfficeName = w.OfficeName,
            IsEnabled = w.IsEnabled,
            CurrentActivityLabel = activity.Label,
            CurrentActivityTone = activity.Tone,
            IsActivityLive = activity.IsLive
        };
    }

    public Task<(bool Success, string? Error)> SetWorkerEnabledAsync(
        Guid workerId,
        bool enabled,
        CancellationToken ct = default) =>
        previewOptions.Value.Enabled
            ? Task.FromResult<(bool, string?)>((true, null))
            : api.SetWorkerEnabledAsync(workerId, enabled, ct);

    public async Task<(BulkWorkersMonitoringResultDto? Result, string? Error)> SetAllWorkersMonitoringAsync(
        bool enabled,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (new BulkWorkersMonitoringResultDto(1, 0, 1), null);
        }

        var (result, error) = await api.SetAllWorkersEnabledAsync(enabled, ct);
        if (result is not null)
        {
            return (result, null);
        }

        // Невалидная сессия — fallback бессмысленен.
        if (error is not null
            && error.Contains("Сессия недействительна", StringComparison.Ordinal))
        {
            return (null, error);
        }

        // Bulk-эндпоинты могут отсутствовать (частичный деплой) или падать — включаем/выключаем по одному.
        return await SetAllWorkersMonitoringFallbackAsync(enabled, ct);
    }

    private async Task<(BulkWorkersMonitoringResultDto? Result, string? Error)> SetAllWorkersMonitoringFallbackAsync(
        bool enabled,
        CancellationToken ct)
    {
        var workers = await api.GetWorkersAsync(ct);
        if (workers is null)
        {
            return (null, "Не удалось получить список воркеров.");
        }

        var updated = 0;
        var unchanged = 0;
        foreach (var worker in workers)
        {
            if (worker.IsEnabled == enabled)
            {
                unchanged++;
                continue;
            }

            var (success, workerError) = await api.SetWorkerEnabledAsync(worker.Id, enabled, ct);
            if (!success)
            {
                return (null, workerError ?? $"Не удалось изменить воркер «{worker.DisplayName}».");
            }

            updated++;
        }

        return (new BulkWorkersMonitoringResultDto(updated, unchanged, workers.Count), null);
    }

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
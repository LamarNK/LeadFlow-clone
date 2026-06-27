using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class WorkersService(OrbitaApiClient api, IOptions<DesignPreviewOptions> previewOptions) : IWorkersService
{
    private const int DefaultPageSize = 12;

    public async Task<WorkersIndexViewModel> GetIndexAsync(
        string? searchQuery = null,
        int page = 1,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);

        if (previewOptions.Value.Enabled)
            return DesignPreviewData.BuildWorkersIndexViewModel(searchQuery, page, DefaultPageSize);

        var workers = await api.GetWorkersAsync(ct) ?? [];
        var rows = workers.Select(MapRow).ToList();

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var q = searchQuery.Trim();
            rows = rows
                .Where(w => w.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return BuildIndexViewModel(rows, searchQuery, page, DefaultPageSize);
    }

    public async Task<WorkerDetailsViewModel?> GetDetailsAsync(Guid id, CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
            return DesignPreviewData.BuildWorkerDetailsViewModel(id);

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

        return WorkerDetailsBuilder.Build(
            apiWorker,
            accountRows,
            workerEvents,
            new WorkerExtraInfoViewModel
            {
                LeadFlowVersion = apiWorker.AppVersion,
                AgentVersion = apiWorker.AppVersion,
                ConnectionCheck = apiWorker.IsOnline ? "Успешно" : "Нет связи"
            });
    }

    private static WorkersIndexViewModel BuildIndexViewModel(
        IReadOnlyList<WorkerRowViewModel> allRows,
        string? searchQuery,
        int page,
        int pageSize)
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
            SearchQuery = searchQuery,
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
            }
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
        LastActivityUtc = w.LastSeenAtUtc
    };
}
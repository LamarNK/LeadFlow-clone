using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class ResponsesService(OrbitaApiClient api, IOptions<DesignPreviewOptions> previewOptions) : IResponsesService
{
    public async Task<ResponsesIndexViewModel> GetIndexAsync(
        string? from,
        string? to,
        string? status,
        Guid? workerId,
        Guid? accountId,
        string? vacancy,
        string? search,
        Guid? selectedId,
        int page = 1,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        var period = DashboardPeriod.Parse(from, to);
        var filters = new ResponsesFilterViewModel
        {
            Status = status,
            WorkerId = workerId,
            AccountId = accountId,
            VacancyQuery = vacancy,
            SearchQuery = search,
            DateFrom = period.From,
            DateTo = period.To,
            Page = page
        };

        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildResponsesIndexViewModel(filters);
        }

        var (fromUtc, toUtc) = ToUtcRange(period);
        var query = BuildQueryParams(status, search, vacancy, workerId, accountId, fromUtc, toUtc, page, ResponsesIndexBuilder.DefaultPageSize);

        var pageDto = await api.GetResponsesPageAsync(query, ct)
            ?? new ResponsesPageDto([], 0, page, ResponsesIndexBuilder.DefaultPageSize);
        var summary = await api.GetResponsesSummaryAsync(query, ct)
            ?? new ResponsesSummaryDto(0, 0, 0, 0, null);
        var workers = await api.GetWorkersAsync(ct) ?? [];
        var accounts = await api.GetResponseFilterAccountsAsync(ct) ?? [];

        ResponseDetailViewModel? selected = null;
        if (selectedId is Guid id)
        {
            var detail = await api.GetResponseDetailAsync(id, ct);
            if (detail is not null)
            {
                selected = ResponsesIndexBuilder.MapDetail(detail);
            }
        }

        return new ResponsesIndexViewModel
        {
            Filters = filters,
            PeriodLabel = period.Label,
            ActivePeriodPreset = period.ActivePreset,
            KpiCards = ResponsesIndexBuilder.BuildKpiCards(summary),
            Statuses = ResponsesIndexBuilder.StatusOptions,
            Workers = ResponsesIndexBuilder.BuildWorkerOptions(workers),
            Accounts = ResponsesIndexBuilder.BuildAccountOptions(accounts),
            Responses = pageDto.Items.Select(ResponsesIndexBuilder.MapRow).ToList(),
            Pagination = new PaginationViewModel
            {
                Page = pageDto.Page,
                PageSize = pageDto.PageSize,
                TotalItems = pageDto.TotalCount
            },
            Selected = selected
        };
    }

    public async Task<(bool Success, string? Error)> ResendToBitrixAsync(Guid id, CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        var result = await api.ResendResponseToBitrixAsync(id, ct);
        if (result is null)
        {
            return (false, "Не удалось выполнить запрос.");
        }

        return result.Success
            ? (true, null)
            : (false, result.ErrorMessage ?? "Отправка не удалась.");
    }

    internal static string BuildQueryParams(
        string? status,
        string? search,
        string? vacancy,
        Guid? workerId,
        Guid? accountId,
        DateTime fromUtc,
        DateTime toUtc,
        int page,
        int pageSize)
    {
        var parts = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}",
            $"from={Uri.EscapeDataString(fromUtc.ToString("o"))}",
            $"to={Uri.EscapeDataString(toUtc.ToString("o"))}"
        };

        if (!string.IsNullOrWhiteSpace(status))
        {
            parts.Add($"status={Uri.EscapeDataString(status)}");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            parts.Add($"search={Uri.EscapeDataString(search)}");
        }

        if (!string.IsNullOrWhiteSpace(vacancy))
        {
            parts.Add($"vacancy={Uri.EscapeDataString(vacancy)}");
        }

        if (workerId is Guid wid)
        {
            parts.Add($"workerId={wid}");
        }

        if (accountId is Guid aid)
        {
            parts.Add($"accountId={aid}");
        }

        return string.Join('&', parts);
    }

    private static (DateTime FromUtc, DateTime ToUtcExclusive) ToUtcRange(DashboardPeriod period)
    {
        var fromLocal = DateTime.SpecifyKind(period.From.Date, DateTimeKind.Local);
        var toExclusiveLocal = DateTime.SpecifyKind(period.To.Date.AddDays(1), DateTimeKind.Local);
        return (fromLocal.ToUniversalTime(), toExclusiveLocal.ToUniversalTime());
    }
}
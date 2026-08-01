using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class ResponsesService(
    OrbitaApiClient api,
    IOfficeContext officeContext,
    IOptions<DesignPreviewOptions> previewOptions) : IResponsesService
{
    public async Task<ResponsesIndexViewModel> GetIndexAsync(
        string? from,
        string? to,
        string? status,
        Guid? workerId,
        Guid? accountId,
        string? bitrixDestination,
        string? gender,
        int? ageFrom,
        int? ageTo,
        string? vacancy,
        string? search,
        Guid? selectedId,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = ListPageSizeDefaults.Normalize(pageSize, ListPageSizeDefaults.Responses);
        var period = DashboardPeriod.Parse(from, to);
        var tableSort = TableSort.Parse(sort, sortDir, TableSort.Responses.Default, TableSort.Responses.Columns);
        var filters = new ResponsesFilterViewModel
        {
            Status = status,
            WorkerId = workerId,
            AccountId = accountId,
            BitrixDestination = bitrixDestination,
            Gender = CandidateGenders.NormalizeFilterValue(gender),
            AgeFrom = NormalizeAgeFilter(ageFrom),
            AgeTo = NormalizeAgeFilter(ageTo),
            VacancyQuery = vacancy,
            SearchQuery = search,
            DateFrom = period.From,
            DateTo = period.To,
            Page = page
        };

        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildResponsesIndexViewModel(filters, selectedId, pageSize.Value, sort, sortDir);
        }

        var (fromUtc, toUtc) = ToUtcRange(period);
        var query = BuildQueryParams(
            status,
            search,
            vacancy,
            workerId,
            accountId,
            bitrixDestination,
            filters.Gender,
            filters.AgeFrom,
            filters.AgeTo,
            fromUtc,
            toUtc,
            page,
            pageSize.Value,
            sort,
            sortDir);

        var pageDto = await api.GetResponsesPageAsync(query, ct)
            ?? new ResponsesPageDto([], 0, page, pageSize.Value);
        var summary = await api.GetResponsesSummaryAsync(query, ct)
            ?? new ResponsesSummaryDto(0, 0, 0, 0, 0, null);
        var workers = await api.GetWorkersAsync(ct) ?? [];
        var accounts = await api.GetResponseFilterAccountsAsync(ct) ?? [];
        var bitrixInstances = await api.GetBitrixInstancesAsync(ct: ct) ?? [];
        var officeOptionsDto = await api.GetOfficeOptionsAsync(ct) ?? [];
        var officeOptions = officeOptionsDto
            .Select(o => new EventFilterOptionViewModel { Value = o.Id.ToString(), Label = o.Name })
            .ToList();
        var deliveryOffices = officeOptionsDto
            .Where(o => o.IsEnabled && o.CrmEnabled)
            .Select(o => new DeliveryOfficeOptionViewModel
            {
                Id = o.Id,
                Name = o.Name,
                CrmEnabled = true
            })
            .ToList();
        var vacancyOptions = await api.GetResponseFilterVacanciesAsync(fromUtc, toUtc, ct) ?? [];

        ResponseDetailViewModel? selected = null;
        if (selectedId is Guid id)
        {
            var detail = await api.GetResponseDetailAsync(id, ct);
            if (detail is not null)
            {
                selected = ResponsesIndexBuilder.MapDetail(detail);
            }
        }

        var workerOptions = ResponsesIndexBuilder.BuildWorkerOptions(workers);
        var accountOptions = ResponsesIndexBuilder.BuildAccountOptions(accounts);
        var bitrixDestinationOptions = ResponsesIndexBuilder.BuildBitrixDestinationOptions(bitrixInstances);
        var genderOptions = ResponsesIndexBuilder.GenderOptions;
        var vacancyFilterOptions = ResponsesIndexBuilder.BuildVacancyOptions(vacancyOptions);
        var activeFilterChips = FilterChipsBuilder.ForResponses(
            filters,
            period,
            ResponsesIndexBuilder.StatusOptions,
            workerOptions,
            accountOptions,
            bitrixDestinationOptions,
            genderOptions,
            pageSize.Value);

        return new ResponsesIndexViewModel
        {
            Header = PageHeaderBuilder.WithOfficeScope(PageHeaderBuilder.ResponsesList(period), officeContext),
            Filters = filters,
            PeriodLabel = period.Label,
            ActivePeriodPreset = period.ActivePreset,
            KpiCards = ResponsesIndexBuilder.BuildKpiCards(summary, period.From, period.To, workerId, accountId),
            Statuses = ResponsesIndexBuilder.StatusOptions,
            Workers = workerOptions,
            Accounts = accountOptions,
            BitrixDestinations = bitrixDestinationOptions,
            Genders = genderOptions,
            Vacancies = vacancyFilterOptions,
            Responses = pageDto.Items.Select(ResponsesIndexBuilder.MapRow).ToList(),
            SendBitrixInstances = ResponsesIndexBuilder.MapSendBitrixInstances(bitrixInstances),
            OfficeOptions = officeOptions,
            DeliveryOffices = deliveryOffices,
            Pagination = new PaginationViewModel
            {
                Page = pageDto.Page,
                PageSize = pageDto.PageSize,
                TotalItems = pageDto.TotalCount
            },
            Selected = selected,
            HasActiveFilters = ResponsesIndexBuilder.HasActiveFilters(filters, period),
            ActiveFilterChips = activeFilterChips,
            Sort = tableSort
        };
    }

    public async Task<ResponseDetailJsonViewModel?> GetDetailJsonAsync(Guid id, CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            var preview = DesignPreviewData.BuildResponsesIndexViewModel(
                new ResponsesFilterViewModel(),
                id);
            return preview.Selected is null ? null : ResponsesIndexBuilder.MapDetailJson(preview.Selected);
        }

        var detail = await api.GetResponseDetailAsync(id, ct);
        return detail is null ? null : ResponsesIndexBuilder.MapDetailJson(ResponsesIndexBuilder.MapDetail(detail));
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

    public async Task<(bool Success, string? Error)> SendToBitrixAsync(
        Guid id,
        Guid bitrixInstanceId,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        var result = await api.SendResponseToBitrixAsync(id, bitrixInstanceId, ct);
        if (result is null)
        {
            return (false, "Не удалось выполнить запрос.");
        }

        return result.Success
            ? (true, null)
            : (false, result.ErrorMessage ?? "Отправка не удалась.");
    }

    public async Task<(BulkSendBitrixResultDto? Result, string? Error)> BulkSendToBitrixAsync(
        IReadOnlyList<Guid> responseIds,
        Guid bitrixInstanceId,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            var items = responseIds
                .Select(id => new BulkSendBitrixItemResultDto(id, true, ResponseStatuses.Sent, null))
                .ToList();
            return (new BulkSendBitrixResultDto(responseIds.Count, responseIds.Count, 0, items), null);
        }

        return await api.BulkSendResponsesToBitrixAsync(responseIds, bitrixInstanceId, ct);
    }

    public async Task<(bool Success, string? Error)> DeliverAsync(
        Guid id,
        IReadOnlyList<Guid> officeIds,
        bool toCrm,
        bool toBitrix,
        IReadOnlyList<Guid> bitrixInstanceIds,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        officeIds = NormalizeIds(officeIds);
        bitrixInstanceIds = NormalizeIds(bitrixInstanceIds);
        var useRoute = bitrixInstanceIds.Count == 0;
        var result = await api.DeliverResponseAsync(
            id,
            new DeliverResponseRequest(
                OfficeId: officeIds.Count == 1 ? officeIds[0] : null,
                ToCrm: toCrm,
                ToBitrix: toBitrix,
                BitrixInstanceId: bitrixInstanceIds.Count == 1 ? bitrixInstanceIds[0] : null,
                UseBitrixRoute: useRoute,
                OfficeIds: officeIds,
                BitrixInstanceIds: bitrixInstanceIds),
            ct);
        if (result is null)
        {
            return (false, "Не удалось выполнить запрос.");
        }

        return result.Success
            ? (true, result.ErrorMessage)
            : (false, result.ErrorMessage ?? "Отправка не удалась.");
    }

    public async Task<(BulkDeliverResponsesResultDto? Result, string? Error)> DeliverBulkAsync(
        IReadOnlyList<Guid> responseIds,
        IReadOnlyList<Guid> officeIds,
        bool toCrm,
        bool toBitrix,
        IReadOnlyList<Guid> bitrixInstanceIds,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            var items = responseIds
                .Select(id => new BulkDeliverItemResultDto(id, true, ResponseStatuses.Sent, null))
                .ToList();
            return (new BulkDeliverResponsesResultDto(responseIds.Count, responseIds.Count, 0, items), null);
        }

        officeIds = NormalizeIds(officeIds);
        bitrixInstanceIds = NormalizeIds(bitrixInstanceIds);
        var useRoute = bitrixInstanceIds.Count == 0;
        return await api.DeliverResponsesBulkAsync(
            new BulkDeliverResponsesRequest(
                responseIds,
                OfficeId: officeIds.Count == 1 ? officeIds[0] : null,
                ToCrm: toCrm,
                ToBitrix: toBitrix,
                BitrixInstanceId: bitrixInstanceIds.Count == 1 ? bitrixInstanceIds[0] : null,
                UseBitrixRoute: useRoute,
                OfficeIds: officeIds,
                BitrixInstanceIds: bitrixInstanceIds),
            ct);
    }

    private static IReadOnlyList<Guid> NormalizeIds(IReadOnlyList<Guid>? ids) =>
        (ids ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

    public async Task<ResponsesDeliverOptionsViewModel> GetDeliverOptionsAsync(CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildResponsesDeliverOptions();
        }

        var officeOptionsDto = await api.GetOfficeOptionsAsync(ct) ?? [];
        var bitrixInstances = await api.GetBitrixInstancesAsync(ct: ct) ?? [];

        return new ResponsesDeliverOptionsViewModel
        {
            // Only offices that can actually receive CRM cards (enabled + CRM on).
            DeliveryOffices = officeOptionsDto
                .Where(o => o.IsEnabled && o.CrmEnabled)
                .Select(o => new DeliveryOfficeOptionViewModel
                {
                    Id = o.Id,
                    Name = o.Name,
                    CrmEnabled = true
                })
                .ToList(),
            SendBitrixInstances = ResponsesIndexBuilder.MapSendBitrixInstances(bitrixInstances)
        };
    }

    internal static string BuildQueryParams(
        string? status,
        string? search,
        string? vacancy,
        Guid? workerId,
        Guid? accountId,
        string? bitrixDestination,
        string? gender,
        int? ageFrom,
        int? ageTo,
        DateTime fromUtc,
        DateTime toUtc,
        int page,
        int pageSize,
        string? sort = null,
        string? sortDir = null)
    {
        var parts = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}",
            $"from={Uri.EscapeDataString(fromUtc.ToString("o"))}",
            $"to={Uri.EscapeDataString(toUtc.ToString("o"))}"
        };

        if (!string.IsNullOrWhiteSpace(sort))
        {
            parts.Add($"sort={Uri.EscapeDataString(sort)}");
        }

        if (!string.IsNullOrWhiteSpace(sortDir))
        {
            parts.Add($"dir={Uri.EscapeDataString(sortDir)}");
        }

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

        if (!string.IsNullOrWhiteSpace(bitrixDestination))
        {
            parts.Add($"bitrixDestination={Uri.EscapeDataString(bitrixDestination)}");
        }

        if (!string.IsNullOrWhiteSpace(gender))
        {
            parts.Add($"gender={Uri.EscapeDataString(gender)}");
        }

        if (ageFrom is int fromAge)
        {
            parts.Add($"ageFrom={fromAge}");
        }

        if (ageTo is int toAge)
        {
            parts.Add($"ageTo={toAge}");
        }

        return string.Join('&', parts);
    }

    private static int? NormalizeAgeFilter(int? value) =>
        value is >= 0 and <= 120 ? value : null;

    private static (DateTime FromUtc, DateTime ToUtcExclusive) ToUtcRange(DashboardPeriod period)
    {
        var fromLocal = DateTime.SpecifyKind(period.From.Date, DateTimeKind.Local);
        var toExclusiveLocal = DateTime.SpecifyKind(period.To.Date.AddDays(1), DateTimeKind.Local);
        return (fromLocal.ToUniversalTime(), toExclusiveLocal.ToUniversalTime());
    }
}
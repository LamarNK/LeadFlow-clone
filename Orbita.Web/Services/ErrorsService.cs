using Microsoft.Extensions.Options;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class ErrorsService(
    OrbitaApiClient api,
    IOfficeContext officeContext,
    IOptions<DesignPreviewOptions> previewOptions) : IErrorsService
{
    public Task<ErrorsIndexViewModel> GetIndexAsync(
        string? q = null,
        string? severity = null,
        string? type = null,
        Guid? workerId = null,
        Guid? accountId = null,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default)
    {
        pageSize = ListPageSizeDefaults.Normalize(pageSize, ListPageSizeDefaults.Errors);
        var filters = new ErrorsFilterViewModel
        {
            SearchQuery = q,
            Severity = severity,
            Type = type,
            WorkerId = workerId,
            AccountId = accountId
        };

        if (previewOptions.Value.Enabled)
            return Task.FromResult(DesignPreviewData.BuildErrorsIndexViewModel(filters, page, pageSize.Value, sort, sortDir));

        return GetFromApiAsync(filters, page, pageSize.Value, sort, sortDir, ct);
    }

    private async Task<ErrorsIndexViewModel> GetFromApiAsync(
        ErrorsFilterViewModel filters,
        int page,
        int pageSize,
        string? sort,
        string? sortDir,
        CancellationToken ct)
    {
        var items = await api.GetEventsAsync(limit: 500, ct: ct) ?? [];
        var rows = items
            .Where(e => e.Level is "Error")
            .Select(e => ErrorsIndexBuilder.MapEvent(e))
            .ToList();

        return ErrorsIndexBuilder.Build(rows, filters, page, pageSize, sort: sort, sortDir: sortDir, officeContext: officeContext);
    }

    public Task<(bool Success, string? Error)> DismissEventAsync(Guid eventId, CancellationToken ct = default) =>
        api.DismissEventAsync(eventId, ct);
}
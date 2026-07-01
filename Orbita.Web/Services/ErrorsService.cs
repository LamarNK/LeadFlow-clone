using Microsoft.Extensions.Options;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class ErrorsService(OrbitaApiClient api, IOptions<DesignPreviewOptions> previewOptions) : IErrorsService
{
    public Task<ErrorsIndexViewModel> GetIndexAsync(
        string? q = null,
        string? severity = null,
        string? type = null,
        Guid? workerId = null,
        string? account = null,
        int page = 1,
        CancellationToken ct = default)
    {
        var filters = new ErrorsFilterViewModel
        {
            SearchQuery = q,
            Severity = severity,
            Type = type,
            WorkerId = workerId,
            Account = account
        };

        if (previewOptions.Value.Enabled)
            return Task.FromResult(DesignPreviewData.BuildErrorsIndexViewModel(filters, page, ErrorsIndexBuilder.DefaultPageSize));

        return GetFromApiAsync(filters, page, ct);
    }

    private async Task<ErrorsIndexViewModel> GetFromApiAsync(
        ErrorsFilterViewModel filters,
        int page,
        CancellationToken ct)
    {
        var todayStartUtc = DateTime.UtcNow.Date;
        var items = await api.GetEventsAsync(limit: 500, ct: ct) ?? [];
        var rows = items
            .Where(e => e.CreatedAtUtc >= todayStartUtc)
            .Where(e => e.Level is "Error" or "Warning")
            .Select(e => ErrorsIndexBuilder.MapEvent(e, e.AccountId?.ToString()[..8]))
            .ToList();

        return ErrorsIndexBuilder.Build(rows, filters, page);
    }

    public Task<(bool Success, string? Error)> DismissEventAsync(Guid eventId, CancellationToken ct = default) =>
        api.DismissEventAsync(eventId, ct);
}
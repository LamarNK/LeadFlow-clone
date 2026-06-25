using Microsoft.Extensions.Options;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class EventsService(OrbitaApiClient api, IOptions<DesignPreviewOptions> previewOptions) : IEventsService
{
    public Task<EventsIndexViewModel> GetIndexAsync(
        string? q = null,
        string? type = null,
        Guid? workerId = null,
        string? account = null,
        string? level = null,
        int page = 1,
        CancellationToken ct = default)
    {
        var filters = new EventsFilterViewModel
        {
            SearchQuery = q,
            Type = type,
            WorkerId = workerId,
            Account = account,
            Level = level
        };

        if (previewOptions.Value.Enabled)
            return Task.FromResult(DesignPreviewData.BuildEventsIndexViewModel(filters, page, EventsIndexBuilder.DefaultPageSize));

        return GetFromApiAsync(filters, page, ct);
    }

    private async Task<EventsIndexViewModel> GetFromApiAsync(
        EventsFilterViewModel filters,
        int page,
        CancellationToken ct)
    {
        var items = await api.GetEventsAsync(limit: 500, ct: ct) ?? [];
        var rows = items
            .Select(e => EventsIndexBuilder.MapEvent(e, e.AccountId?.ToString()[..8]))
            .ToList();

        return EventsIndexBuilder.Build(rows, filters, page);
    }
}
using Microsoft.Extensions.Options;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class EventsService(
    OrbitaApiClient api,
    IErrorsService errors,
    IOptions<DesignPreviewOptions> previewOptions) : IEventsService
{
    public async Task<EventsIndexViewModel> GetIndexAsync(
        string? q = null,
        string? type = null,
        Guid? workerId = null,
        Guid? accountId = null,
        string? level = null,
        string? view = null,
        string? severity = null,
        int page = 1,
        CancellationToken ct = default)
    {
        var journalView = NormalizeJournalView(view);

        if (journalView == "errors")
        {
            var errorsPage = await errors.GetIndexAsync(q, severity, type, workerId, accountId, page, ct);
            return new EventsIndexViewModel
            {
                Header = PageHeaderBuilder.EventsList(),
                JournalView = journalView,
                ErrorsPage = errorsPage,
                HasActiveFilters = errorsPage.HasActiveFilters,
                ActiveFilterChips = errorsPage.ActiveFilterChips,
                KpiCards = errorsPage.KpiCards
            };
        }

        var filters = new EventsFilterViewModel
        {
            SearchQuery = q,
            Type = type,
            WorkerId = workerId,
            AccountId = accountId,
            Level = journalView == "warnings" ? "warning" : level
        };

        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildEventsIndexViewModel(
                filters,
                page,
                EventsIndexBuilder.DefaultPageSize,
                journalView);
        }

        return await GetFromApiAsync(filters, page, journalView, ct);
    }

    private async Task<EventsIndexViewModel> GetFromApiAsync(
        EventsFilterViewModel filters,
        int page,
        string journalView,
        CancellationToken ct)
    {
        var items = await api.GetEventsAsync(limit: 500, ct: ct) ?? [];
        var rows = items
            .Select(e => EventsIndexBuilder.MapEvent(e))
            .ToList();

        return EventsIndexBuilder.Build(rows, filters, page, journalView: journalView);
    }

    public Task<(bool Success, string? Error)> DismissEventAsync(Guid eventId, CancellationToken ct = default) =>
        api.DismissEventAsync(eventId, ct);

    private static string NormalizeJournalView(string? view) =>
        view?.Trim().ToLowerInvariant() switch
        {
            "warnings" => "warnings",
            "errors" => "errors",
            _ => "all"
        };
}
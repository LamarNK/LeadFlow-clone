using Microsoft.Extensions.Options;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class EventsService(
    OrbitaApiClient api,
    IOfficeContext officeContext,
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
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default)
    {
        var journalView = NormalizeJournalView(view);

        if (journalView == "errors")
        {
            pageSize = ListPageSizeDefaults.Normalize(pageSize, ListPageSizeDefaults.Errors);
            var errorsPage = await errors.GetIndexAsync(q, severity, type, workerId, accountId, page, pageSize, sort, sortDir, ct);
            return new EventsIndexViewModel
            {
                Header = PageHeaderBuilder.WithOfficeScope(PageHeaderBuilder.EventsList(), officeContext),
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

        pageSize = ListPageSizeDefaults.Normalize(pageSize, ListPageSizeDefaults.Events);

        if (previewOptions.Value.Enabled)
        {
            return DesignPreviewData.BuildEventsIndexViewModel(
                filters,
                page,
                pageSize.Value,
                journalView,
                sort,
                sortDir);
        }

        return await GetFromApiAsync(filters, page, pageSize.Value, journalView, sort, sortDir, ct);
    }

    private async Task<EventsIndexViewModel> GetFromApiAsync(
        EventsFilterViewModel filters,
        int page,
        int pageSize,
        string journalView,
        string? sort,
        string? sortDir,
        CancellationToken ct)
    {
        var items = await api.GetEventsAsync(limit: 500, ct: ct) ?? [];
        var rows = items
            .Select(e => EventsIndexBuilder.MapEvent(e))
            .ToList();

        return EventsIndexBuilder.Build(
            rows,
            filters,
            page,
            pageSize: pageSize,
            journalView: journalView,
            sort: sort,
            sortDir: sortDir,
            officeContext: officeContext);
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
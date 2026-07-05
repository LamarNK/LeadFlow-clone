using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IEventsService
{
    Task<EventsIndexViewModel> GetIndexAsync(
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
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> DismissEventAsync(Guid eventId, CancellationToken ct = default);
}
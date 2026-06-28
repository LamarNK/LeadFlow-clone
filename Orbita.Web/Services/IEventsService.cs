using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IEventsService
{
    Task<EventsIndexViewModel> GetIndexAsync(
        string? q = null,
        string? type = null,
        Guid? workerId = null,
        string? account = null,
        string? level = null,
        int page = 1,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> DismissEventAsync(Guid eventId, CancellationToken ct = default);
}
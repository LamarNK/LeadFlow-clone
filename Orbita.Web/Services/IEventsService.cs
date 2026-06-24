using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IEventsService
{
    Task<EventsIndexViewModel> GetIndexAsync(bool errorsOnly = false, CancellationToken ct = default);
}
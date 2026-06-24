using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public sealed class EventsService(OrbitaApiClient api) : IEventsService
{
    public async Task<EventsIndexViewModel> GetIndexAsync(bool errorsOnly = false, CancellationToken ct = default)
    {
        var all = await api.GetEventsAsync(limit: errorsOnly ? 200 : 100, ct: ct) ?? [];
        var events = errorsOnly
            ? all.Where(e => e.Level is "Error" or "Warning").ToList()
            : all;

        return new EventsIndexViewModel
        {
            Header = new PageHeaderViewModel
            {
                Title = errorsOnly ? "Ошибки" : "События",
                Subtitle = errorsOnly ? "События с уровнем Error и Warning" : "Последние события по всем воркерам",
                ShowRefresh = true,
                UpdatedAt = DateTime.Now
            },
            Events = events
        };
    }
}
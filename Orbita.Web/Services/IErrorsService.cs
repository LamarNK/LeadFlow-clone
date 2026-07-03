using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IErrorsService
{
    Task<ErrorsIndexViewModel> GetIndexAsync(
        string? q = null,
        string? severity = null,
        string? type = null,
        Guid? workerId = null,
        Guid? accountId = null,
        int page = 1,
        CancellationToken ct = default);

    Task<(bool Success, string? Error)> DismissEventAsync(Guid eventId, CancellationToken ct = default);
}
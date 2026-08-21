using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IAccountsService
{
    Task<AccountsIndexViewModel> GetIndexAsync(
        string? searchQuery = null,
        string? tab = null,
        Guid? workerId = null,
        string? groupId = null,
        int page = 1,
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default);
}
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IAccountsService
{
    Task<AccountsIndexViewModel> GetIndexAsync(
        string? searchQuery = null,
        string? tab = null,
        int page = 1,
        string? sort = null,
        string? sortDir = null,
        CancellationToken ct = default);
}
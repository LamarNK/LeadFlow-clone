using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IAccountsService
{
    Task<AccountsIndexViewModel> GetIndexAsync(string? searchQuery = null, string? tab = null, int page = 1, CancellationToken ct = default);
}
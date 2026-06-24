using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public interface IAccountsService
{
    Task<AccountsIndexViewModel> GetIndexAsync(CancellationToken ct = default);
}
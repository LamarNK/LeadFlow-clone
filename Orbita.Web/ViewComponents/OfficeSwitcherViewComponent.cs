using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Helpers;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.ViewComponents;

public sealed class OfficeSwitcherViewComponent(
    IOfficeContext officeContext,
    OrbitaApiClient api,
    IHttpContextAccessor httpContextAccessor) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!officeContext.IsAdmin)
        {
            return View(new OfficeSwitcherViewModel());
        }

        var offices = await api.GetOfficesAsync(HttpContext.RequestAborted) ?? [];
        var returnUrl = OfficeSwitchReturnUrl.Build(httpContextAccessor.HttpContext?.Request);
        var model = new OfficeSwitcherViewModel
        {
            IsVisible = true,
            IsAllOfficesSelected = officeContext.ShowAllOffices,
            SelectedOfficeId = officeContext.EffectiveOfficeId,
            SelectedLabel = officeContext.ShowAllOffices
                ? "Все офисы"
                : officeContext.ContextLabel?.Replace("Офис: ", string.Empty, StringComparison.Ordinal) ?? "Офис",
            Offices = offices
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new OfficeSwitcherOptionViewModel
                {
                    Id = x.Id,
                    Name = x.Name,
                    IsEnabled = x.IsEnabled
                })
                .ToList(),
            ReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl!
        };

        return View(model);
    }
}

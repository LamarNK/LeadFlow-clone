using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Authorization;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class OfficeController(OrbitaApiClient api) : Controller
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = OrbitaRoles.Admin)]
    public async Task<IActionResult> Select(Guid? officeId, string? returnUrl, CancellationToken ct)
    {
        var target = NormalizeReturnUrl(returnUrl);
        if (officeId is null)
        {
            ClearOfficeCookies();
            return Redirect(target);
        }

        var office = await api.GetOfficeAsync(officeId.Value, ct);
        if (office is null || !office.IsEnabled)
        {
            ClearOfficeCookies();
            return Redirect(target);
        }

        var expires = DateTimeOffset.UtcNow.AddDays(30);
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = expires
        };

        Response.Cookies.Append(OfficeSelection.CookieName, office.Id.ToString("D"), cookieOptions);
        Response.Cookies.Append(OfficeSelection.NameCookieName, office.Name, cookieOptions);
        return Redirect(target);
    }

    private void ClearOfficeCookies()
    {
        Response.Cookies.Delete(OfficeSelection.CookieName);
        Response.Cookies.Delete(OfficeSelection.NameCookieName);
    }

    private string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !Url.IsLocalUrl(returnUrl))
        {
            return Url.Action("Index", "Dashboard") ?? "/";
        }

        return returnUrl;
    }
}
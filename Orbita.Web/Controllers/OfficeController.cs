using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Orbita.Contracts;
using Orbita.Web.Authorization;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class OfficeController(OrbitaApiClient api) : Controller
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PanelPermissions.Administration)]
    public async Task<IActionResult> Select(Guid? officeId, string? returnUrl, CancellationToken ct)
    {
        var target = NormalizeReturnUrl(returnUrl);
        if (officeId is null)
        {
            ClearOfficeCookies();
            return Redirect(SynchronizeOfficeIdQuery(target, null));
        }

        var office = await api.GetOfficeAsync(officeId.Value, ct);
        if (office is null || !office.IsEnabled)
        {
            ClearOfficeCookies();
            return Redirect(SynchronizeOfficeIdQuery(target, null));
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
        return Redirect(SynchronizeOfficeIdQuery(target, office.Id));
    }

    internal static string SynchronizeOfficeIdQuery(string target, Guid? officeId)
    {
        var fragmentIndex = target.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? target[fragmentIndex..] : string.Empty;
        var targetWithoutFragment = fragmentIndex >= 0 ? target[..fragmentIndex] : target;
        var queryIndex = targetWithoutFragment.IndexOf('?');
        if (queryIndex < 0)
        {
            return target;
        }

        var path = targetWithoutFragment[..queryIndex];
        var query = QueryHelpers.ParseQuery(targetWithoutFragment[(queryIndex + 1)..]);
        if (!query.ContainsKey("officeId"))
        {
            return target;
        }

        var parameters = query
            .Where(pair => !string.Equals(pair.Key, "officeId", StringComparison.OrdinalIgnoreCase))
            .SelectMany(
                pair => pair.Value,
                (pair, value) => new KeyValuePair<string, string?>(pair.Key, value))
            .ToList();

        if (officeId is Guid selectedOfficeId)
        {
            parameters.Add(new KeyValuePair<string, string?>("officeId", selectedOfficeId.ToString("D")));
        }

        return path + QueryString.Create(parameters).Value + fragment;
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

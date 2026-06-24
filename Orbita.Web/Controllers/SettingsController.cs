using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class SettingsController(ThemeService theme) : Controller
{
    [HttpGet]
    public IActionResult Index() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ToggleTheme()
    {
        theme.Toggle();
        Response.Cookies.Append(ThemeService.CookieName, theme.Theme, new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });
        var referer = Request.Headers.Referer.ToString();
        return Redirect(string.IsNullOrWhiteSpace(referer) ? "/" : referer);
    }
}
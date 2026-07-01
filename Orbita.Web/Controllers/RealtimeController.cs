using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class RealtimeController(AuthSession session) : Controller
{
    [HttpGet]
    public IActionResult AccessToken()
    {
        var token = session.Token;
        if (string.IsNullOrWhiteSpace(token)
            || string.Equals(token, AuthSession.DesignPreviewToken, StringComparison.Ordinal))
        {
            return Unauthorized(new { error = "Сессия недействительна." });
        }

        // Same-origin URL: browser must not use OrbitaApi:BaseUrl (often internal http://api:8080 in Docker).
        var hubPath = $"{Request.PathBase}/hubs/panel".Replace("//", "/");
        if (!hubPath.StartsWith('/'))
        {
            hubPath = "/" + hubPath;
        }

        return Json(new
        {
            hubUrl = hubPath,
            accessToken = token
        });
    }
}
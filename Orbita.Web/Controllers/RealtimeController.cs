using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class RealtimeController(IConfiguration configuration, AuthSession session) : Controller
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

        // Prefer public API URL in production: WebSocket through Web→YARP→API often fails behind reverse proxies.
        // OrbitaApi:BaseUrl is internal (http://api:8080 in Docker) and must not be sent to the browser.
        var hubUrl = BuildHubUrl();

        var captchaHubUrl = BuildCaptchaHubUrl();
        var browserMonitorHubUrl = BuildBrowserMonitorHubUrl();

        return Json(new
        {
            hubUrl,
            captchaHubUrl,
            browserMonitorHubUrl,
            accessToken = token
        });
    }

    private string BuildHubUrl() => BuildHubUrl("/hubs/panel");

    private string BuildCaptchaHubUrl() => BuildHubUrl("/hubs/captcha");

    private string BuildBrowserMonitorHubUrl() => BuildHubUrl("/hubs/browser-monitor");

    private string BuildHubUrl(string hubSuffix)
    {
        var publicApiBase = configuration["OrbitaApi:PublicBaseUrl"]?.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(publicApiBase))
        {
            return $"{publicApiBase}{hubSuffix}";
        }

        var hubPath = $"{Request.PathBase}{hubSuffix}".Replace("//", "/");
        if (!hubPath.StartsWith('/'))
        {
            hubPath = "/" + hubPath;
        }

        return hubPath;
    }
}
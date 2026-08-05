using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Logging.Audit;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

public sealed class AccountController(
    OrbitaApiClient api,
    OrbitaAuthService auth,
    IOptions<DesignPreviewOptions> previewOptions) : Controller
{
    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login()
    {
        var preview = previewOptions.Value;
        return View(new LoginViewModel
        {
            Email = preview.Enabled ? preview.Email : string.Empty,
            DesignPreviewEnabled = preview.Enabled
        });
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult AccessDenied()
    {
        return View(ErrorPageViewModel.ForStatusCode(StatusCodes.Status403Forbidden));
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        LoginResponse? result;
        try
        {
            result = await api.LoginAsync(model.Email, model.Password, ct);
        }
        catch (HttpRequestException ex)
        {
            await GlobalLogger.Instance.LogAsync(
                $"Login API unreachable: {ex.Message}",
                DeskLinkAuditLogLevel.Error,
                errorKey: "auth.login.api_unreachable");
            model.ErrorMessage = previewOptions.Value.Enabled
                ? "Не удалось войти в режиме просмотра. Проверьте email и пароль из appsettings."
                : "API недоступен. Запустите PostgreSQL и Orbita.Api (https://localhost:7291).";
            return View(model);
        }

        if (result is null)
        {
            await GlobalLogger.Instance.LogAsync(
                $"Login failed in panel ({model.Email}).",
                DeskLinkAuditLogLevel.Warning,
                errorKey: "auth.login.invalid_credentials");
            model.ErrorMessage = "Неверный email или пароль.";
            return View(model);
        }

        await GlobalLogger.Instance.LogAsync(
            $"Login succeeded in panel ({result.Email}).",
            DeskLinkAuditLogLevel.Info);

        if (string.Equals(result.Token, AuthSession.DesignPreviewToken, StringComparison.Ordinal))
        {
            await auth.SignInPreviewAsync(result.Email, previewOptions.Value.DisplayName, ct);
            return RedirectToAction("Index", "Dashboard");
        }

        await auth.SignInAsync(result.Token, result.Email, ct);

        // The cookie principal is refreshed only on the next request, so choose the
        // landing page from the permissions embedded in the just-issued JWT.
        var (action, controller) = GetInitialDestination(result.Token);
        return RedirectToAction(action, controller);
    }

    private static (string Action, string Controller) GetInitialDestination(string token)
    {
        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            var permissions = jwt.Claims
                .Where(c => c.Type == PanelPermissions.ClaimType)
                .Select(c => c.Value)
                .ToHashSet(StringComparer.Ordinal);

            if (permissions.Contains(PanelPermissions.Dashboard)) return ("Index", "Dashboard");
            if (permissions.Contains(PanelPermissions.Crm)) return ("Index", "Crm");
            if (permissions.Contains(PanelPermissions.Workers)) return ("Index", "Workers");
            if (permissions.Contains(PanelPermissions.Accounts)) return ("Index", "Accounts");
            if (permissions.Contains(PanelPermissions.Statistics)) return ("Index", "Statistics");
            if (permissions.Contains(PanelPermissions.Responses)) return ("Index", "Responses");
            if (permissions.Contains(PanelPermissions.Events)) return ("Index", "Events");
            if (permissions.Contains(PanelPermissions.Settings)) return ("Index", "MySettings");
            if (permissions.Contains(PanelPermissions.Administration)) return ("Index", "Settings");
        }
        catch
        {
            // Authentication already validated the token before reaching this point.
        }

        return ("Login", "Account");
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.Identity?.Name;
        await auth.SignOutAsync(ct);
        await GlobalLogger.Instance.LogAsync(
            $"Panel logout{(string.IsNullOrWhiteSpace(email) ? "" : $" ({email})")}.",
            DeskLinkAuditLogLevel.Info);
        return RedirectToAction(nameof(Login));
    }
}

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

        // Managers land in CRM workspace; everyone else on the dashboard.
        // User principal is not yet refreshed on this request — read roles from the JWT.
        if (IsManagerOnlyToken(result.Token))
        {
            return RedirectToAction("Index", "Crm");
        }

        return RedirectToAction("Index", "Dashboard");
    }

    private static bool IsManagerOnlyToken(string token)
    {
        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            var roles = jwt.Claims
                .Where(c => c.Type is ClaimTypes.Role or "role")
                .Select(c => c.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return roles.Contains(PanelRoles.Manager) && !roles.Contains(PanelRoles.Admin);
        }
        catch
        {
            return false;
        }
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
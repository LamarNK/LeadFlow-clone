using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Logging.Audit;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

public sealed class AccountController(OrbitaApiClient api, OrbitaAuthService auth) : Controller
{
    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login() => View(new LoginViewModel { Email = "admin@orbita.local" });

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
            model.ErrorMessage = "API недоступен. Запустите PostgreSQL и Orbita.Api (https://localhost:7291).";
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
        await auth.SignInAsync(result.Token, result.Email, ct);
        return RedirectToAction("Index", "Dashboard");
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.SignOutAsync(ct);
        return RedirectToAction(nameof(Login));
    }
}
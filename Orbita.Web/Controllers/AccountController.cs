using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

        var result = await api.LoginAsync(model.Email, model.Password, ct);
        if (result is null)
        {
            model.ErrorMessage = "Неверный email или пароль.";
            return View(model);
        }

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
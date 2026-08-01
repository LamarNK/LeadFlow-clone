using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Authorization;
using Orbita.Web.Helpers;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize]
public sealed class MySettingsController(
    IMySettingsService settings,
    BitrixValidationResultCache bitrixValidationCache) : Controller
{
    private bool IsManagerOnly =>
        User.IsInRole(OrbitaRoles.Manager) && !User.IsInRole(OrbitaRoles.Admin);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet]
    public async Task<IActionResult> Index(
        string? tab,
        Guid? instanceId,
        CancellationToken ct = default)
    {
        var model = await settings.GetIndexAsync(tab, instanceId, ct);
        var editor = model.BitrixInstances?.Editor;
        if (model.ActiveTab == "bitrix"
            && editor is not null
            && TempData["BitrixValidationCacheKey"] is string cacheKey)
        {
            var cached = bitrixValidationCache.Take(cacheKey);
            if (cached is not null)
            {
                editor = editor.WithLiveValidation(cached.DraftWebhookUrl, cached.Validation);
            }
        }

        if (model.BitrixInstances is not null && editor is not null && editor != model.BitrixInstances.Editor)
        {
            model = model with
            {
                BitrixInstances = new BitrixInstancesRegistryViewModel
                {
                    OfficeId = model.BitrixInstances.OfficeId,
                    OfficeName = model.BitrixInstances.OfficeName,
                    CanManage = model.BitrixInstances.CanManage,
                    CanManageTransmission = model.BitrixInstances.CanManageTransmission,
                    TransmissionEnabled = model.BitrixInstances.TransmissionEnabled,
                    Instances = model.BitrixInstances.Instances,
                    Editor = editor
                }
            };
        }

        model = model with
        {
            StatusMessage = TempData["MySettingsStatus"] as string ?? model.StatusMessage,
            ErrorMessage = TempData["MySettingsError"] as string ?? model.ErrorMessage
        };
        return View(model);
    }

    [HttpGet]
    public IActionResult CreateBitrixInstance(string? tab = "bitrix")
    {
        if (IsManagerOnly)
        {
            return RedirectToAction(nameof(Index), new { tab = "profile" });
        }

        return RedirectToAction(nameof(Index), new { tab, instanceId = Guid.Empty });
    }

    [HttpGet]
    public IActionResult EditBitrixInstance(Guid id, string? tab = "bitrix")
    {
        if (IsManagerOnly)
        {
            return RedirectToAction(nameof(Index), new { tab = "profile" });
        }

        return RedirectToAction(nameof(Index), new { tab, instanceId = id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeOwnPassword(ChangeOwnPasswordFormModel model, CancellationToken ct = default)
    {
        if (!string.Equals(model.NewPassword, model.ConfirmPassword, StringComparison.Ordinal))
        {
            TempData["MySettingsError"] = "Новый пароль и подтверждение не совпадают.";
            return RedirectToAction(nameof(Index), new { tab = "profile" });
        }

        var (success, error) = await settings.ChangeOwnPasswordAsync(model.CurrentPassword, model.NewPassword, ct);
        TempData[success ? "MySettingsStatus" : "MySettingsError"] = success
            ? "Пароль изменён."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "profile" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBitrixTransmission(SaveBitrixTransmissionFormModel model, CancellationToken ct = default)
    {
        if (IsManagerOnly)
        {
            return Forbid();
        }

        var transmissionEnabled = FormBindingHelper.ReadCheckbox(Request.Form, "TransmissionEnabled");
        var (success, error) = await settings.SaveBitrixTransmissionAsync(transmissionEnabled, ct);
        TempData[success ? "MySettingsStatus" : "MySettingsError"] = success
            ? transmissionEnabled
                ? "Автораспределение откликов включено."
                : "Автораспределение откликов отключено."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "bitrix" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBitrixInstance(SaveBitrixInstanceFormModel model, CancellationToken ct = default)
    {
        if (IsManagerOnly)
        {
            return Forbid();
        }

        var (success, error, instanceId) = await settings.SaveBitrixInstanceAsync(model, ct);
        TempData[success ? "MySettingsStatus" : "MySettingsError"] = success
            ? model.Id is Guid existingId && existingId != Guid.Empty
                ? "Битрикс обновлён."
                : "Битрикс создан."
            : error;
        return RedirectToAction(nameof(Index), new
        {
            tab = "bitrix",
            instanceId = success ? instanceId : model.Id
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBitrixInstance(Guid id, CancellationToken ct = default)
    {
        if (IsManagerOnly)
        {
            return Forbid();
        }

        var (success, error) = await settings.DeleteBitrixInstanceAsync(id, ct);
        TempData[success ? "MySettingsStatus" : "MySettingsError"] = success
            ? "Битрикс удалён."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "bitrix" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ValidateBitrixInstance(
        ValidateBitrixInstanceFormModel model,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        if (IsManagerOnly)
        {
            return Forbid();
        }

        var webhookUrl = string.IsNullOrWhiteSpace(model.WebhookUrl) ? null : model.WebhookUrl.Trim();
        var (validation, error) = model.Id != Guid.Empty
            ? await api.ValidateBitrixInstanceAsync(model.Id, webhookUrl, ct: ct)
            : await api.ValidateBitrixWebhookAsync(webhookUrl, ct: ct);
        if (validation is null)
        {
            return BadRequest(new { error = error ?? "Не удалось выполнить проверку вебхука." });
        }

        return Json(validation);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDistributionRoute(
        SaveDistributionRouteFormModel model,
        CancellationToken ct = default)
    {
        if (IsManagerOnly)
        {
            return Forbid();
        }

        IReadOnlyList<SaveDistributionNodeRequest> nodes;
        try
        {
            nodes = JsonSerializer.Deserialize<List<SaveDistributionNodeRequest>>(model.NodesJson, JsonOptions)
                ?? [];
        }
        catch
        {
            TempData["MySettingsError"] = "Некорректная схема связей.";
            return RedirectToAction(nameof(Index), new { tab = "distribution" });
        }

        var autoEnabled = FormBindingHelper.ReadCheckbox(Request.Form, "IsAutoDistributionEnabled");
        var bitrixLeadQuotas = FormBindingHelper.ParseBitrixLeadQuotas(model.BitrixQuotasJson);
        var (success, error) = await settings.SaveDistributionRouteAsync(
            autoEnabled,
            nodes,
            bitrixLeadQuotas,
            ct);
        TempData[success ? "MySettingsStatus" : "MySettingsError"] = success
            ? "Схема связей сохранена."
            : error;
        return RedirectToAction(nameof(Index), new { tab = "distribution" });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDistributionRouteJson(
        [FromBody] SaveDistributionRouteRequest request,
        [FromServices] OrbitaApiClient api,
        CancellationToken ct = default)
    {
        if (IsManagerOnly)
        {
            return Forbid();
        }

        var (route, error) = await api.SaveDistributionRouteAsync(request, ct: ct);
        if (route is null)
        {
            return BadRequest(new { error });
        }

        return Json(route);
    }
}
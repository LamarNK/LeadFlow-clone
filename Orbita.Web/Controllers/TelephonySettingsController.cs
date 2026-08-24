using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Web.Controllers;

[Authorize(Policy = "OfficeStaff")]
[Route("Settings")]
public sealed class TelephonySettingsController(
    OrbitaApiClient api,
    IOfficeContext officeContext) : Controller
{
    [HttpGet("Telephony")]
    public async Task<IActionResult> Telephony(
        Guid officeId,
        string provider = CrmTelephonyProviders.Asterisk,
        CancellationToken ct = default)
    {
        if (!CrmTelephonyProviders.IsSupported(provider)) return BadRequest();
        provider = CrmTelephonyProviders.Normalize(provider);

        var profile = await api.GetPanelProfileAsync(ct);
        if (profile is null) return Forbid();

        var isAdmin = User.IsInRole(PanelRoles.Admin);
        IReadOnlyList<OfficeDto>? adminOffices = null;
        Guid? effectiveOfficeId;
        if (isAdmin)
        {
            adminOffices = await api.GetOfficesAsync(ct) ?? [];
            effectiveOfficeId = ResolveAdminOfficeId(
                officeId,
                officeContext.EffectiveOfficeId,
                profile.OfficeId,
                adminOffices.FirstOrDefault()?.Id);
        }
        else
        {
            effectiveOfficeId = profile.OfficeId;
        }

        if (effectiveOfficeId is not Guid selectedOfficeId
            || (!isAdmin && officeId != Guid.Empty && officeId != selectedOfficeId))
        {
            return Forbid();
        }

        var sipoutSettingsTask = api.GetCrmTelephonySettingsAsync(selectedOfficeId, ct, CrmTelephonyProviders.Sipout);
        var plusofonSettingsTask = api.GetCrmTelephonySettingsAsync(selectedOfficeId, ct, CrmTelephonyProviders.Plusofon);
        var asteriskSettingsTask = api.GetCrmTelephonySettingsAsync(selectedOfficeId, ct, CrmTelephonyProviders.Asterisk);
        var beelineSettingsTask = api.GetCrmTelephonySettingsAsync(selectedOfficeId, ct, CrmTelephonyProviders.Beeline);
        await Task.WhenAll(sipoutSettingsTask, plusofonSettingsTask, asteriskSettingsTask, beelineSettingsTask);
        var providerSettings = new Dictionary<string, CrmTelephonySettingsDto?>(StringComparer.OrdinalIgnoreCase)
        {
            [CrmTelephonyProviders.Sipout] = await sipoutSettingsTask,
            [CrmTelephonyProviders.Plusofon] = await plusofonSettingsTask,
            [CrmTelephonyProviders.Asterisk] = await asteriskSettingsTask,
            [CrmTelephonyProviders.Beeline] = await beelineSettingsTask
        };
        var selectedSettings = providerSettings[provider];
        if (selectedSettings is null) return NotFound();

        string? officeName;
        IReadOnlyList<PanelUserDto> users;
        if (isAdmin)
        {
            var offices = adminOffices ?? [];
            officeName = offices.FirstOrDefault(x => x.Id == selectedOfficeId)?.Name;
            users = (await api.GetPanelUsersAsync(ct) ?? [])
                .Where(x => x.OfficeId == selectedOfficeId)
                .ToList();
        }
        else
        {
            officeName = profile.OfficeName;
            users = await api.GetOfficeStaffUsersAsync(selectedOfficeId, ct) ?? [];
        }

        if (string.IsNullOrWhiteSpace(officeName)) return NotFound();
        users = users
            .OrderBy(x => string.IsNullOrWhiteSpace(x.FullName) ? x.Email : x.FullName)
            .ToList();

        return View("~/Views/Settings/Telephony.cshtml", new CrmTelephonyPageViewModel
        {
            OfficeId = selectedOfficeId,
            OfficeName = officeName,
            Settings = selectedSettings,
            PhoneUsers = providerSettings[CrmTelephonyProviders.Asterisk]?.UserBindings ?? [],
            OfficeUsers = users,
            ProviderSummaries =
            [
                ToProviderSummary(
                    providerSettings[CrmTelephonyProviders.Asterisk],
                    "SIP-сервер",
                    "Звонки из браузера через линии Плюсофон и Билайн",
                    "fa-server"),
                ToProviderSummary(
                    providerSettings[CrmTelephonyProviders.Sipout],
                    "SIPOUT",
                    "События и записи из существующей браузерной звонилки",
                    "fa-phone-volume"),
                ToProviderSummary(
                    providerSettings[CrmTelephonyProviders.Plusofon],
                    "Плюсофон API",
                    "Синхронизация завершённых звонков и аудиозаписей",
                    "fa-cloud-arrow-down"),
                ToProviderSummary(
                    providerSettings[CrmTelephonyProviders.Beeline],
                    "Билайн SIP",
                    "Транк Билайна для исходящих и входящих звонков",
                    "fa-tower-cell")
            ],
            Provider = provider,
            CanManage = User.IsInRole(PanelRoles.Admin) || User.IsInRole(PanelRoles.OfficeLead),
            ProviderSetupUrl = TempData["TelephonyProviderSetupUrl"] as string,
            WebhookSecret = TempData["TelephonyWebhookSecret"] as string,
            WebhookSecretHeader = TempData["TelephonyWebhookSecretHeader"] as string,
            StatusMessage = TempData["SettingsStatus"] as string,
            ErrorMessage = TempData["SettingsError"] as string
        });
    }

    internal static Guid? ResolveAdminOfficeId(
        Guid requestedOfficeId,
        Guid? selectedOfficeId,
        Guid? profileOfficeId,
        Guid? firstAvailableOfficeId)
    {
        if (requestedOfficeId != Guid.Empty)
        {
            return requestedOfficeId;
        }

        return selectedOfficeId ?? profileOfficeId ?? firstAvailableOfficeId;
    }

    [HttpPost("Telephony/RotateReceiver")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateTelephonyReceiver(
        Guid officeId,
        string provider,
        CancellationToken ct = default)
    {
        if (!CrmTelephonyProviders.IsSupported(provider)) return BadRequest();
        provider = CrmTelephonyProviders.Normalize(provider);
        var (receiver, error) = await api.RotateCrmTelephonyReceiverAsync(officeId, ct, provider);
        if (receiver is null)
        {
            TempData["SettingsError"] = error;
        }
        else
        {
            TempData["SettingsStatus"] = $"Новый защищённый приёмник {provider.ToUpperInvariant()} создан. Скопируйте данные сейчас: секрет повторно не показывается.";
            TempData["TelephonyProviderSetupUrl"] = receiver.SipoutWebRequestUrl;
            TempData["TelephonyWebhookSecret"] = receiver.WebhookSecret;
            TempData["TelephonyWebhookSecretHeader"] = receiver.WebhookSecretHeader;
        }
        return RedirectToAction(nameof(Telephony), new { officeId, provider });
    }

    [HttpPost("Telephony/Enabled")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetTelephonyEnabled(
        Guid officeId,
        bool enabled,
        string provider,
        CancellationToken ct = default)
    {
        if (!CrmTelephonyProviders.IsSupported(provider)) return BadRequest();
        provider = CrmTelephonyProviders.Normalize(provider);
        var (success, error) = await api.SetCrmTelephonyEnabledAsync(officeId, enabled, ct, provider);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? enabled ? "Приём звонков включён." : "Приём звонков приостановлен."
            : error;
        return RedirectToAction(nameof(Telephony), new { officeId, provider });
    }

    [HttpPost("Telephony/Plusofon")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePlusofonCredentials(
        Guid officeId,
        string clientId,
        string accessToken,
        CancellationToken ct = default)
    {
        var (success, error) = await api.SetPlusofonCredentialsAsync(officeId, clientId, accessToken, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Реквизиты API Плюсофона сохранены в защищённом виде. Орбита будет забирать записи завершённых звонков автоматически."
            : error;
        return RedirectToAction(nameof(Telephony), new { officeId, provider = CrmTelephonyProviders.Plusofon });
    }

    [HttpPost("Telephony/SipAccount")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSipProviderAccount(
        SaveSipProviderAccountFormModel model,
        CancellationToken ct = default)
    {
        if (!string.Equals(model.Provider, CrmTelephonyProviders.Beeline, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest();
        }

        var request = new UpdateSipProviderAccountRequest(
                model.Server,
                model.Domain,
                model.Port,
                model.Transport,
                model.SipLogin,
                model.AuthorizationLogin,
                model.Password,
                model.UseForOutbound,
                model.Name,
                model.Mode);
        var (success, error) = string.IsNullOrWhiteSpace(model.AccountKey)
            ? await api.AddSipProviderAccountAsync(model.OfficeId, CrmTelephonyProviders.Beeline, request, ct)
            : await api.UpdateSipProviderAccountAsync(model.OfficeId, CrmTelephonyProviders.Beeline, model.AccountKey, request, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Линия Билайна сохранена и передана Asterisk. Статус регистрации обновится в течение нескольких секунд."
            : error;
        return RedirectToAction(nameof(Telephony), new { officeId = model.OfficeId, provider = CrmTelephonyProviders.Beeline });
    }

    [HttpPost("Telephony/SipAccount/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSipProviderAccount(
        Guid officeId,
        string accountKey,
        CancellationToken ct = default)
    {
        var (success, error) = await api.DeleteSipProviderAccountAsync(
            officeId, CrmTelephonyProviders.Beeline, accountKey, ct);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Линия Билайна удалена."
            : error;
        return RedirectToAction(nameof(Telephony), new { officeId, provider = CrmTelephonyProviders.Beeline });
    }

    [HttpPost("Telephony/Binding")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTelephonyBinding(
        SaveCrmTelephonyBindingFormModel model,
        CancellationToken ct = default)
    {
        if (!CrmTelephonyProviders.IsSupported(model.Provider)) return BadRequest();
        var provider = CrmTelephonyProviders.Normalize(model.Provider);
        var (binding, error) = await api.SetCrmTelephonyBindingAsync(
            model.OfficeId, model.UserId, model.ProviderUserKey, ct, provider, model.OutboundProvider);
        TempData[binding is null ? "SettingsError" : "SettingsStatus"] = binding is null
            ? error
            : $"{provider.ToUpperInvariant()} {binding.ProviderUserKey} привязан к сотруднику {binding.UserName}.";
        return RedirectToAction(nameof(Telephony), new { officeId = model.OfficeId, provider });
    }

    [HttpPost("Telephony/Binding/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTelephonyBinding(
        Guid officeId,
        string userId,
        string provider,
        CancellationToken ct = default)
    {
        if (!CrmTelephonyProviders.IsSupported(provider)) return BadRequest();
        provider = CrmTelephonyProviders.Normalize(provider);
        var (success, error) = await api.RemoveCrmTelephonyBindingAsync(officeId, userId, ct, provider);
        TempData[success ? "SettingsStatus" : "SettingsError"] = success
            ? "Привязка телефонии удалена."
            : error;
        return RedirectToAction(nameof(Telephony), new { officeId, provider });
    }

    private static CrmTelephonyProviderSummaryViewModel ToProviderSummary(
        CrmTelephonySettingsDto? settings,
        string name,
        string description,
        string icon) => new(
        settings?.Provider ?? string.Empty,
        name,
        description,
        icon,
        settings?.IsConfigured == true,
        settings?.IsEnabled == true,
        settings?.UserBindings.Count ?? 0);
}

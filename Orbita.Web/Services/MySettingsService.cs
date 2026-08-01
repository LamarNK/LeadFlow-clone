using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class MySettingsService(OrbitaApiClient api, IOptions<DesignPreviewOptions> previewOptions) : IMySettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyList<SettingsTabViewModel> OperatorTabs =
    [
        new() { Id = "profile", Label = "Мой профиль" },
        new() { Id = "bitrix", Label = "Битриксы" },
        new() { Id = "distribution", Label = "Связи" }
    ];

    private static readonly IReadOnlyList<SettingsTabViewModel> ManagerOnlyTabs =
    [
        new() { Id = "profile", Label = "Мой профиль" }
    ];

    public async Task<MySettingsIndexViewModel> GetIndexAsync(
        string? tab,
        Guid? instanceId = null,
        CancellationToken ct = default)
    {
        var profile = await BuildProfileAsync(ct);
        var isManagerOnly = profile?.Role is PanelRoles.Manager;
        var tabs = isManagerOnly ? ManagerOnlyTabs : OperatorTabs;
        // Bitrix/distribution are operator office tools — managers only edit their profile.
        var activeTab = isManagerOnly ? "profile" : NormalizeTab(tab);

        return new MySettingsIndexViewModel
        {
            Header = PageHeaderBuilder.MySettings(),
            ActiveTab = activeTab,
            Tabs = tabs,
            Profile = profile,
            BitrixInstances = activeTab == "bitrix" ? await BuildBitrixInstancesAsync(instanceId, ct) : null,
            Distribution = activeTab == "distribution" ? await BuildDistributionAsync(ct) : null
        };
    }

    public Task<(bool Success, string? Error)> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken ct = default) =>
        api.ChangeOwnPasswordAsync(currentPassword, newPassword, ct);

    public async Task<(bool Success, string? Error)> SaveBitrixTransmissionAsync(
        bool transmissionEnabled,
        CancellationToken ct = default)
    {
        var (settings, error) = await api.UpdateOfficeBitrixSettingsAsync(transmissionEnabled, ct: ct);
        return settings is not null ? (true, null) : (false, error);
    }

    public async Task<(bool Success, string? Error, Guid? InstanceId)> SaveBitrixInstanceAsync(
        SaveBitrixInstanceFormModel model,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null, model.Id ?? DesignPreviewData.PreviewBitrixInstanceId);
        }

        if (model.Id is Guid existingId && existingId != Guid.Empty)
        {
            var (instance, error) = await api.UpdateBitrixInstanceAsync(
                existingId,
                new UpdateBitrixInstanceRequest(
                    model.Name,
                    model.Signature,
                    string.IsNullOrWhiteSpace(model.WebhookUrl) ? null : model.WebhookUrl.Trim(),
                    null,
                    model.IsEnabled),
                ct: ct);
            return instance is null ? (false, error, null) : (true, null, instance.Id);
        }

        if (string.IsNullOrWhiteSpace(model.WebhookUrl))
        {
            return (false, "Укажите URL вебхука для нового Битрикса.", null);
        }

        var (created, createError) = await api.CreateBitrixInstanceAsync(
            new CreateBitrixInstanceRequest(
                model.Name,
                model.Signature,
                model.WebhookUrl.Trim(),
                null,
                model.IsEnabled),
            ct: ct);
        return created is null ? (false, createError, null) : (true, null, created.Id);
    }

    public async Task<(bool Success, string? Error)> DeleteBitrixInstanceAsync(Guid id, CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        var (success, error) = await api.DeleteBitrixInstanceAsync(id, ct: ct);
        return success ? (true, null) : (false, error);
    }

    public async Task<(bool Success, string? Error)> SaveDistributionRouteAsync(
        bool isAutoDistributionEnabled,
        IReadOnlyList<SaveDistributionNodeRequest> nodes,
        IReadOnlyList<SaveBitrixLeadQuotaRequest>? bitrixLeadQuotas = null,
        CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            return (true, null);
        }

        var (route, error) = await api.SaveDistributionRouteAsync(
            new SaveDistributionRouteRequest(isAutoDistributionEnabled, nodes, bitrixLeadQuotas),
            ct: ct);
        return route is not null ? (true, null) : (false, error);
    }

    private async Task<ProfileSettingsViewModel?> BuildProfileAsync(CancellationToken ct)
    {
        var profile = await api.GetPanelProfileAsync(ct);
        var policy = await api.GetPasswordPolicyAsync(ct);
        if (profile is null)
        {
            return null;
        }

        return new ProfileSettingsViewModel
        {
            Email = profile.Email,
            Role = profile.Role,
            RoleLabel = profile.Role switch
            {
                PanelRoles.Admin => "Администратор",
                PanelRoles.Manager => "Менеджер",
                _ => "Оператор"
            },
            PasswordPolicy = policy is null ? null : MapPasswordPolicy(policy)
        };
    }

    private async Task<BitrixInstancesRegistryViewModel?> BuildBitrixInstancesAsync(
        Guid? instanceId,
        CancellationToken ct)
    {
        var officeBitrixSettings = await api.GetOfficeBitrixSettingsAsync(ct: ct);
        var instances = await api.GetBitrixInstancesAsync(ct: ct) ?? [];
        var canManage = officeBitrixSettings is not null;

        BitrixInstanceEditorViewModel? editor = null;
        if (instanceId == Guid.Empty)
        {
            editor = CreateNewEditor();
        }
        else if (instanceId is Guid selectedId)
        {
            var detail = await api.GetBitrixInstanceAsync(selectedId, ct: ct);
            if (detail is not null)
            {
                editor = MapEditor(detail);
            }
        }

        return new BitrixInstancesRegistryViewModel
        {
            OfficeId = officeBitrixSettings?.OfficeId,
            OfficeName = officeBitrixSettings?.OfficeName,
            CanManage = canManage,
            CanManageTransmission = officeBitrixSettings is not null,
            TransmissionEnabled = officeBitrixSettings?.TransmissionEnabled ?? true,
            Instances = instances.Select(MapListItem).ToList(),
            Editor = editor
        };
    }

    private async Task<DistributionEditorViewModel?> BuildDistributionAsync(CancellationToken ct)
    {
        var officeBitrixSettings = await api.GetOfficeBitrixSettingsAsync(ct: ct);
        var canManage = officeBitrixSettings is not null;
        var instances = await api.GetBitrixInstancesAsync(ct: ct) ?? [];
        var route = await api.GetDistributionRouteAsync(ct: ct)
            ?? new DistributionRouteDto(Guid.Empty, officeBitrixSettings?.OfficeId ?? Guid.Empty, false, [], null);

        var instancePayload = instances
            .Where(x => x.IsEnabled)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.Signature,
                Label = FormatBitrixLabel(x.Name, x.Signature),
                x.LeadExportLimit,
                x.LeadExportSessionCount
            })
            .ToList();

        return new DistributionEditorViewModel
        {
            OfficeId = officeBitrixSettings?.OfficeId,
            OfficeName = officeBitrixSettings?.OfficeName,
            CanManage = canManage,
            IsAutoDistributionEnabled = route.IsAutoDistributionEnabled,
            RouteJson = JsonSerializer.Serialize(route, JsonOptions),
            InstancesJson = JsonSerializer.Serialize(instancePayload, JsonOptions)
        };
    }

    internal static BitrixInstanceListItemViewModel MapListItem(BitrixInstanceListItemDto item)
    {
        var (label, tone) = MapValidationStatus(item.ValidationStatus);
        return new BitrixInstanceListItemViewModel
        {
            Id = item.Id,
            Name = item.Name,
            Signature = item.Signature,
            DisplayLabel = FormatBitrixLabel(item.Name, item.Signature),
            PortalHost = item.PortalHost,
            ValidationStatus = item.ValidationStatus,
            ValidationMessage = item.ValidationMessage,
            ValidationStatusLabel = label,
            ValidationStatusTone = tone,
            IsEnabled = item.IsEnabled
        };
    }

    internal static BitrixInstanceEditorViewModel MapEditor(BitrixInstanceDto detail)
    {
        var (label, tone) = MapValidationStatus(detail.ValidationStatus);
        return new BitrixInstanceEditorViewModel
        {
            Id = detail.Id,
            IsNew = false,
            Name = detail.Name,
            Signature = detail.Signature,
            DisplayLabel = FormatBitrixLabel(detail.Name, detail.Signature),
            MaskedWebhookUrl = detail.MaskedWebhookUrl,
            PortalHost = detail.PortalHost,
            ValidationStatus = detail.ValidationStatus,
            ValidationMessage = detail.ValidationMessage,
            LastValidatedAtUtc = detail.LastValidatedAtUtc,
            ValidationStatusLabel = label,
            ValidationStatusTone = tone,
            IsEnabled = detail.IsEnabled
        };
    }

    internal static BitrixInstanceEditorViewModel CreateNewEditor() =>
        new()
        {
            IsNew = true,
            ValidationStatus = BitrixValidationStatuses.NotConfigured,
            ValidationStatusLabel = "Новый",
            ValidationStatusTone = "neutral"
        };

    internal static string FormatBitrixLabel(string name, string signature) =>
        !string.IsNullOrWhiteSpace(signature) ? signature
        : !string.IsNullOrWhiteSpace(name) ? name
        : "—";

    private static PasswordPolicyViewModel MapPasswordPolicy(PasswordPolicyDto policy)
    {
        var parts = new List<string> { $"минимум {policy.RequiredLength} символов" };
        if (policy.RequireDigit) parts.Add("цифра");
        if (policy.RequireLowercase) parts.Add("строчная буква");
        if (policy.RequireUppercase) parts.Add("заглавная буква");
        if (policy.RequireNonAlphanumeric) parts.Add("спецсимвол");
        if (policy.RequiredUniqueChars > 1) parts.Add($"{policy.RequiredUniqueChars} уникальных символов");

        return new PasswordPolicyViewModel
        {
            RequiredLength = policy.RequiredLength,
            RequireDigit = policy.RequireDigit,
            RequireLowercase = policy.RequireLowercase,
            RequireUppercase = policy.RequireUppercase,
            RequireNonAlphanumeric = policy.RequireNonAlphanumeric,
            RequiredUniqueChars = policy.RequiredUniqueChars,
            Summary = string.Join(", ", parts)
        };
    }

    private static (string Label, string Tone) MapValidationStatus(string status) =>
        status switch
        {
            BitrixValidationStatuses.Ok => ("Подключено", "success"),
            BitrixValidationStatuses.Warning => ("Ограничения", "warning"),
            BitrixValidationStatuses.Error => ("Ошибка", "error"),
            _ => ("Не настроено", "neutral")
        };

    private static string NormalizeTab(string? tab) =>
        tab?.Trim().ToLowerInvariant() switch
        {
            "bitrix" => "bitrix",
            "distribution" or "links" or "svyazi" => "distribution",
            _ => "profile"
        };
}

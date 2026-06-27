using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class MySettingsService(OrbitaApiClient api, IOptions<DesignPreviewOptions> previewOptions) : IMySettingsService
{
    private static readonly IReadOnlyList<SettingsTabViewModel> Tabs =
    [
        new() { Id = "profile", Label = "Мой профиль" },
        new() { Id = "bitrix", Label = "Bitrix24" }
    ];

    public async Task<MySettingsIndexViewModel> GetIndexAsync(string? tab, CancellationToken ct = default)
    {
        var activeTab = string.Equals(tab, "bitrix", StringComparison.OrdinalIgnoreCase) ? "bitrix" : "profile";
        var profile = await BuildProfileAsync(ct);
        var bitrix = activeTab == "bitrix" ? await BuildBitrixAsync(ct) : null;

        return new MySettingsIndexViewModel
        {
            ActiveTab = activeTab,
            Tabs = Tabs,
            Profile = profile,
            Bitrix = bitrix
        };
    }

    public Task<(bool Success, string? Error)> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken ct = default) =>
        api.ChangeOwnPasswordAsync(currentPassword, newPassword, ct);

    public async Task<(bool Success, string? Error)> SaveBitrixAsync(string webhookUrl, CancellationToken ct = default)
    {
        var (integration, error) = await api.SaveMyBitrixIntegrationAsync(webhookUrl, ct);
        return integration is not null ? (true, null) : (false, error);
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
            RoleLabel = profile.Role == PanelRoles.Admin ? "Администратор" : "Оператор",
            PasswordPolicy = policy is null ? null : MapPasswordPolicy(policy)
        };
    }

    private async Task<BitrixSettingsViewModel?> BuildBitrixAsync(CancellationToken ct)
    {
        var integration = await api.GetMyBitrixIntegrationAsync(ct);
        if (integration is null)
        {
            return new BitrixSettingsViewModel
            {
                ValidationStatus = BitrixValidationStatuses.NotConfigured,
                ValidationStatusLabel = "Не настроено",
                ValidationStatusTone = "neutral"
            };
        }

        var (label, tone) = MapValidationStatus(integration.ValidationStatus);
        return new BitrixSettingsViewModel
        {
            MaskedWebhookUrl = integration.MaskedWebhookUrl,
            PortalHost = integration.PortalHost,
            ValidationStatus = integration.ValidationStatus,
            ValidationMessage = integration.ValidationMessage,
            LastValidatedAtUtc = integration.LastValidatedAtUtc,
            ValidationStatusLabel = label,
            ValidationStatusTone = tone
        };
    }

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
}
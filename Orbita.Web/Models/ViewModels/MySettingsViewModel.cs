using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed record MySettingsIndexViewModel
{
    public PageHeaderViewModel? Header { get; init; }

    public required string ActiveTab { get; init; }
    public required IReadOnlyList<SettingsTabViewModel> Tabs { get; init; }
    public ProfileSettingsViewModel? Profile { get; init; }
    public BitrixInstancesRegistryViewModel? BitrixInstances { get; init; }
    public DistributionEditorViewModel? Distribution { get; init; }
    public string? StatusMessage { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class BitrixInstancesRegistryViewModel
{
    public Guid? OfficeId { get; init; }
    public string? OfficeName { get; init; }
    public bool CanManage { get; init; }
    public bool CanManageTransmission { get; init; }
    public bool TransmissionEnabled { get; init; } = true;
    public IReadOnlyList<BitrixInstanceListItemViewModel> Instances { get; init; } = [];
    public BitrixInstanceEditorViewModel? Editor { get; init; }
}

public sealed class BitrixInstanceListItemViewModel
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string Signature { get; init; } = string.Empty;
    public string DisplayLabel { get; init; } = string.Empty;
    public string? PortalHost { get; init; }
    public required string ValidationStatus { get; init; }
    public string? ValidationMessage { get; init; }
    public string ValidationStatusLabel { get; init; } = string.Empty;
    public string ValidationStatusTone { get; init; } = "neutral";
    public bool IsEnabled { get; init; } = true;
}

public sealed class BitrixInstanceEditorViewModel
{
    public Guid? Id { get; init; }
    public bool IsNew { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Signature { get; init; } = string.Empty;
    public string DisplayLabel { get; init; } = string.Empty;
    public string? MaskedWebhookUrl { get; init; }
    public string? PortalHost { get; init; }
    public required string ValidationStatus { get; init; }
    public string? ValidationMessage { get; init; }
    public DateTime? LastValidatedAtUtc { get; init; }
    public string ValidationStatusLabel { get; init; } = string.Empty;
    public string ValidationStatusTone { get; init; } = "neutral";
    public bool IsEnabled { get; init; } = true;
    public string? DraftWebhookUrl { get; init; }
    public BitrixWebhookValidationDto? LiveValidation { get; init; }
    public BitrixInstanceIntegrationSettingsDto IntegrationSettings { get; init; } = new(
        "Deal",
        0,
        "Авито",
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        true);

    public BitrixInstanceEditorViewModel WithLiveValidation(
        string? draftWebhookUrl,
        BitrixWebhookValidationDto? liveValidation) =>
        new()
        {
            Id = Id,
            IsNew = IsNew,
            Name = Name,
            Signature = Signature,
            DisplayLabel = DisplayLabel,
            MaskedWebhookUrl = MaskedWebhookUrl,
            PortalHost = PortalHost,
            ValidationStatus = ValidationStatus,
            ValidationMessage = ValidationMessage,
            LastValidatedAtUtc = LastValidatedAtUtc,
            ValidationStatusLabel = ValidationStatusLabel,
            ValidationStatusTone = ValidationStatusTone,
            IsEnabled = IsEnabled,
            DraftWebhookUrl = draftWebhookUrl,
            LiveValidation = liveValidation,
            IntegrationSettings = IntegrationSettings
        };
}

public sealed class DistributionEditorViewModel
{
    public Guid? OfficeId { get; init; }
    public string? OfficeName { get; init; }
    public bool CanManage { get; init; }
    public bool IsAutoDistributionEnabled { get; init; }
    public required string RouteJson { get; init; }
    public required string InstancesJson { get; init; }
}

public sealed class SaveBitrixTransmissionFormModel
{
    public bool TransmissionEnabled { get; set; }
}

public sealed class SaveBitrixInstanceFormModel
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string? WebhookUrl { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string EntityType { get; set; } = "Deal";
    public int ResponsibleId { get; set; }
    public string LeadSource { get; set; } = "Авито";
    public string DealIdempotencyUfCode { get; set; } = string.Empty;
    public string DealAgeUfCode { get; set; } = string.Empty;
    public string DealProfessionUfCode { get; set; } = string.Empty;
    public string DealCityUfCode { get; set; } = string.Empty;
    public bool CheckDuplicatesInBitrix { get; set; } = true;
}

public sealed class ValidateBitrixInstanceFormModel
{
    public Guid Id { get; set; }
    public string? WebhookUrl { get; set; }
}

public sealed class SaveDistributionRouteFormModel
{
    public bool IsAutoDistributionEnabled { get; set; }
    public string NodesJson { get; set; } = "[]";
    public string BitrixQuotasJson { get; set; } = "[]";
}

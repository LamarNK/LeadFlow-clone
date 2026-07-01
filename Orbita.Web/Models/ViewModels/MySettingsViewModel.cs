namespace Orbita.Web.Models.ViewModels;

public sealed record MySettingsIndexViewModel
{
    public PageHeaderViewModel? Header { get; init; }

    public required string ActiveTab { get; init; }
    public required IReadOnlyList<SettingsTabViewModel> Tabs { get; init; }
    public ProfileSettingsViewModel? Profile { get; init; }
    public BitrixSettingsViewModel? Bitrix { get; init; }
    public string? StatusMessage { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class BitrixSettingsViewModel
{
    public string? MaskedWebhookUrl { get; init; }
    public string? PortalHost { get; init; }
    public required string ValidationStatus { get; init; }
    public string? ValidationMessage { get; init; }
    public DateTime? LastValidatedAtUtc { get; init; }
    public string ValidationStatusLabel { get; init; } = string.Empty;
    public string ValidationStatusTone { get; init; } = "neutral";
    public IReadOnlyList<OfficeBitrixWebhookRowViewModel> OfficeWebhooks { get; init; } = [];
    public bool CanManageTransmission { get; init; }
    public bool TransmissionEnabled { get; init; } = true;
    public string? OfficeName { get; init; }
}

public sealed class SaveBitrixTransmissionFormModel
{
    public bool TransmissionEnabled { get; set; } = true;
}

public sealed class OfficeBitrixWebhookRowViewModel
{
    public string Email { get; init; } = string.Empty;
    public string? PortalHost { get; init; }
    public string ValidationStatusLabel { get; init; } = string.Empty;
    public string ValidationStatusTone { get; init; } = "neutral";
    public bool IsPrimaryForIngestion { get; init; }
}

public sealed class SaveBitrixIntegrationFormModel
{
    public string WebhookUrl { get; set; } = string.Empty;
}
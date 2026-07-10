namespace Orbita.Contracts;

public sealed record OfficeDto(
    Guid Id,
    string Name,
    bool IsEnabled,
    DateTime CreatedAtUtc,
    int WorkerCount,
    int UserCount,
    string BitrixValidationStatus = BitrixValidationStatuses.NotConfigured,
    string? BitrixPortalHost = null);

public sealed record OfficeDetailDto(
    Guid Id,
    string Name,
    bool IsEnabled,
    bool BitrixTransmissionEnabled,
    DateTime CreatedAtUtc,
    bool RegistrationConfigured,
    string MaskedRegistrationSecret,
    string BitrixValidationStatus = BitrixValidationStatuses.NotConfigured,
    string? BitrixValidationMessage = null,
    string? MaskedBitrixWebhookUrl = null,
    string? BitrixPortalHost = null,
    DateTime? BitrixLastValidatedAtUtc = null);

public sealed record OfficeBitrixIntegrationDto(
    Guid OfficeId,
    string OfficeName,
    string? MaskedWebhookUrl,
    string? PortalHost,
    string ValidationStatus,
    string? ValidationMessage,
    DateTime? LastValidatedAtUtc,
    DateTime? UpdatedAtUtc,
    bool TransmissionEnabled);

public sealed record CreateOfficeRequest(string Name);

public sealed record UpdateOfficeRequest(string Name, bool IsEnabled, bool BitrixTransmissionEnabled = true);

public sealed record OfficeBitrixSettingsDto(
    Guid OfficeId,
    string OfficeName,
    bool TransmissionEnabled);

public sealed record UpdateOfficeBitrixSettingsRequest(bool TransmissionEnabled);

public sealed record RotateOfficeRegistrationSecretResponse(
    Guid OfficeId,
    string RegistrationSecret);

public sealed record OfficeRegistrationInfoDto(
    Guid OfficeId,
    string OfficeName,
    bool IsConfigured,
    string MaskedSecret);

public static class OfficeClaims
{
    public const string OfficeId = "office_id";
}

public static class PanelAuditOfficeActions
{
    public const string OfficeCreated = "office.created";
    public const string OfficeUpdated = "office.updated";
    public const string OfficeRegistrationRotated = "office.registration_rotated";
    public const string UserOfficeUpdated = "user.office_updated";
    public const string BitrixTransmissionUpdated = "office.bitrix_transmission_updated";
    public const string BitrixWebhookUpdated = "office.bitrix_webhook_updated";
    public const string BitrixWebhookValidated = "office.bitrix_webhook_validated";
    public const string BitrixInstanceCreated = "bitrix.instance_created";
    public const string BitrixInstanceUpdated = "bitrix.instance_updated";
    public const string BitrixInstanceDeleted = "bitrix.instance_deleted";
    public const string BitrixInstanceValidated = "bitrix.instance_validated";
    public const string DistributionRouteUpdated = "distribution.route_updated";
}
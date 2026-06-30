namespace Orbita.Contracts;

public sealed record OfficeDto(
    Guid Id,
    string Name,
    bool IsEnabled,
    DateTime CreatedAtUtc,
    int WorkerCount,
    int UserCount);

public sealed record OfficeDetailDto(
    Guid Id,
    string Name,
    bool IsEnabled,
    bool BitrixTransmissionEnabled,
    DateTime CreatedAtUtc,
    bool RegistrationConfigured,
    string MaskedRegistrationSecret);

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
}
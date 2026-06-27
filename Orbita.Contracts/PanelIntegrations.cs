namespace Orbita.Contracts;

public sealed record BitrixIntegrationDto(
    string UserId,
    string? MaskedWebhookUrl,
    string? PortalHost,
    string ValidationStatus,
    string? ValidationMessage,
    DateTime? LastValidatedAtUtc,
    DateTime? UpdatedAtUtc);

public sealed record BitrixIntegrationListItemDto(
    string UserId,
    string Email,
    string Role,
    string? PortalHost,
    string ValidationStatus,
    string? ValidationMessage,
    DateTime? LastValidatedAtUtc);

public sealed record SaveBitrixIntegrationRequest(string WebhookUrl);

public sealed record ValidateBitrixIntegrationRequest(string? WebhookUrl);

public sealed record BitrixWebhookValidationDto(
    string Status,
    string Message,
    IReadOnlyList<BitrixValidationStepDto> Steps);

public sealed record BitrixValidationStepDto(
    string Id,
    string Title,
    string Status,
    string Message,
    string? Hint = null);

public static class BitrixValidationStatuses
{
    public const string NotConfigured = "not_configured";
    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Error = "error";
}

public static class BitrixValidationStepStatuses
{
    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Skipped = "skipped";
}
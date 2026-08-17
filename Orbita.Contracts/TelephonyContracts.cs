namespace Orbita.Contracts;

public static class CrmTelephonyProviders
{
    public const string Sipout = "sipout";
}

public static class CrmCallDirections
{
    public const string Incoming = "incoming";
    public const string Outgoing = "outgoing";
    public const string Unknown = "unknown";
}

public sealed record CrmTelephonyUserBindingDto(
    string UserId,
    string UserName,
    string ProviderUserKey);

public sealed record CrmTelephonySettingsDto(
    Guid OfficeId,
    string Provider,
    bool IsConfigured,
    bool IsEnabled,
    Guid? PublicId,
    IReadOnlyList<CrmTelephonyUserBindingDto> UserBindings);

public sealed record CrmTelephonyReceiverDto(
    Guid OfficeId,
    string Provider,
    Guid PublicId,
    string CallbackUrl,
    string SipoutWebRequestUrl);

public sealed record UpdateCrmTelephonyBindingRequest(
    string UserId,
    string ProviderUserKey);

public sealed record UpdateCrmTelephonyEnabledRequest(bool IsEnabled);

public sealed record SipoutCallWebhookPayload(
    string ExternalCallId,
    string? CallerPhone,
    string? CalledPhone,
    string? CallType,
    string? ProviderUserKey,
    string? LastCaller,
    string? StartedAt,
    string? DurationSeconds,
    string? RecordingUrl);

public enum SipoutCallReceiveOutcome
{
    Accepted,
    Updated,
    Unmatched,
    Unauthorized,
    Invalid
}

public sealed record SipoutCallReceiveResult(
    SipoutCallReceiveOutcome Outcome,
    Guid? CallId = null,
    Guid? CardId = null,
    string? Message = null);

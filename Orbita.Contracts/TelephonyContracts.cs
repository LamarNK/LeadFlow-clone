namespace Orbita.Contracts;

public static class CrmTelephonyProviders
{
    public const string Sipout = "sipout";
    public const string Plusofon = "plusofon";
    public const string Asterisk = "asterisk";
    public const string Beeline = "beeline";

    public static bool IsSupported(string? provider) =>
        string.Equals(provider, Sipout, StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, Plusofon, StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, Asterisk, StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, Beeline, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string provider) => provider.Trim().ToLowerInvariant();
}

public static class CrmTelephonyOutboundProviders
{
    public const string Default = "default";

    public static bool IsSupported(string? provider) =>
        string.IsNullOrWhiteSpace(provider)
        || string.Equals(provider, Default, StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, CrmTelephonyProviders.Plusofon, StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, CrmTelephonyProviders.Beeline, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? provider) => string.IsNullOrWhiteSpace(provider)
        ? Default
        : provider.Trim().ToLowerInvariant();
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
    string ProviderUserKey,
    string OutboundProvider = CrmTelephonyOutboundProviders.Default);

public sealed record CrmTelephonySettingsDto(
    Guid OfficeId,
    string Provider,
    bool IsConfigured,
    bool IsEnabled,
    Guid? PublicId,
    IReadOnlyList<CrmTelephonyUserBindingDto> UserBindings,
    bool ProviderCredentialsConfigured = false,
    CrmSipProviderAccountDto? SipAccount = null);

public sealed record CrmSipProviderAccountDto(
    string Server,
    string Domain,
    int Port,
    string Transport,
    string SipLogin,
    string AuthorizationLogin,
    bool PasswordConfigured,
    bool UseForOutbound,
    string RegistrationStatus,
    DateTime? StatusCheckedAtUtc);

public sealed record CrmTelephonyReceiverDto(
    Guid OfficeId,
    string Provider,
    Guid PublicId,
    string CallbackUrl,
    string SipoutWebRequestUrl,
    string? WebhookSecret = null,
    string? WebhookSecretHeader = null);

/// <summary>
/// Short-lived browser SIP configuration for the currently authenticated Orbita user.
/// Provider trunk credentials are intentionally never exposed here.
/// </summary>
public sealed record CrmTelephonyWebRtcConfigDto(
    string WebSocketUrl,
    string SipUri,
    string SipDomain,
    string Extension,
    string AuthorizationUsername,
    string Password);

public sealed record UpdateCrmTelephonyBindingRequest(
    string UserId,
    string ProviderUserKey,
    string? OutboundProvider = null);

public sealed record UpdateCrmTelephonyEnabledRequest(bool IsEnabled);

public sealed record UpdatePlusofonCredentialsRequest(
    string ClientId,
    string AccessToken);

public sealed record UpdateSipProviderAccountRequest(
    string Server,
    string? Domain,
    int Port,
    string Transport,
    string SipLogin,
    string AuthorizationLogin,
    string? Password,
    bool UseForOutbound);

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

public sealed record PlusofonCallWebhookPayload(
    string ExternalCallId,
    string? CallerPhone,
    string? CalledPhone,
    string? Direction,
    string? InternalNumber,
    string? StartedAt,
    string? DurationSeconds,
    string? RecordingUrl);

public sealed record AsteriskCallWebhookPayload(
    string ExternalCallId,
    string? CallerPhone,
    string? CalledPhone,
    string? Direction,
    string? InternalNumber,
    string? StartedAt,
    string? DurationSeconds);

public enum AsteriskInboundRouteOutcome
{
    Resolved,
    Unauthorized,
    Invalid
}

/// <summary>
/// Inbound callback routing derived from the latest Asterisk outbound call.
/// Fallback extensions may answer the call, but never replace the preferred
/// manager or CRM responsible person.
/// </summary>
public sealed record AsteriskInboundRouteResult(
    AsteriskInboundRouteOutcome Outcome,
    string? PreferredExtension = null,
    IReadOnlyList<string>? FallbackExtensions = null,
    DateTime? AffinityExpiresAtUtc = null,
    string? Message = null);

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

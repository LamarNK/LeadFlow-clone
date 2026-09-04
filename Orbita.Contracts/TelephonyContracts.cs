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
    public const string SipoutLinePrefix = "sipout:";
    public const string BeelineLinePrefix = "beeline:";
    public const string PlusofonLinePrefix = "plusofon:";

    public static bool IsSupported(string? provider) =>
        string.IsNullOrWhiteSpace(provider)
        || string.Equals(provider, Default, StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, CrmTelephonyProviders.Sipout, StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, CrmTelephonyProviders.Plusofon, StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, CrmTelephonyProviders.Beeline, StringComparison.OrdinalIgnoreCase)
        || TryGetSipoutLineKey(provider, out _)
        || TryGetBeelineLineKey(provider, out _)
        || TryGetPlusofonLineKey(provider, out _);

    public static string Normalize(string? provider) => string.IsNullOrWhiteSpace(provider)
        ? Default
        : provider.Trim().ToLowerInvariant();

    public static string ForBeelineLine(string accountKey) =>
        $"{BeelineLinePrefix}{accountKey.Trim().ToLowerInvariant()}";

    public static string ForSipoutLine(string accountKey) =>
        $"{SipoutLinePrefix}{accountKey.Trim().ToLowerInvariant()}";

    public static string ForPlusofonLine(string accountKey) =>
        $"{PlusofonLinePrefix}{accountKey.Trim().ToLowerInvariant()}";

    public static bool TryGetSipoutLineKey(string? provider, out string accountKey)
        => TryGetLineKey(provider, SipoutLinePrefix, out accountKey);

    public static bool TryGetBeelineLineKey(string? provider, out string accountKey)
        => TryGetLineKey(provider, BeelineLinePrefix, out accountKey);

    public static bool TryGetPlusofonLineKey(string? provider, out string accountKey)
        => TryGetLineKey(provider, PlusofonLinePrefix, out accountKey);

    private static bool TryGetLineKey(string? provider, string prefix, out string accountKey)
    {
        accountKey = string.Empty;
        if (string.IsNullOrWhiteSpace(provider)
            || !provider.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = provider[prefix.Length..].Trim().ToLowerInvariant();
        if (candidate.Length is 0 or > 16 || !candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            return false;
        }

        accountKey = candidate;
        return true;
    }
}

public static class CrmSipAccountModes
{
    public const string Shared = "shared";
    public const string Personal = "personal";

    public static bool IsSupported(string? mode) =>
        string.Equals(mode, Shared, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, Personal, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string mode) => mode.Trim().ToLowerInvariant();
}

public static class CrmCallDirections
{
    public const string Incoming = "incoming";
    public const string Outgoing = "outgoing";
    public const string Unknown = "unknown";
}

public static class CrmCallStatuses
{
    public const string Answered = "answered";
    public const string Missed = "missed";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string Unknown = "unknown";

    public static bool IsUnanswered(string? status) =>
        status is Missed or Rejected or Failed;
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
    CrmSipProviderAccountDto? SipAccount = null,
    IReadOnlyList<CrmSipProviderAccountDto>? SipAccounts = null,
    IReadOnlyList<CrmTelephonyProviderAccountDto>? ProviderAccounts = null);

public sealed record CrmTelephonyProviderAccountDto(
    Guid Id,
    Guid OfficeId,
    string Provider,
    string Name,
    string? ExternalAccountId,
    IReadOnlyList<string> OwnedNumbers,
    bool CredentialsConfigured,
    bool IsEnabled,
    Guid PublicId,
    DateTime SyncFromUtc,
    DateTime? SyncCursorUtc,
    DateTime? LastSyncedAtUtc,
    string SyncStatus,
    string? LastSyncError,
    int BoundUsersCount,
    IReadOnlyList<CrmTelephonyUserBindingDto>? UserBindings = null);

public sealed record CreateCrmTelephonyProviderAccountRequest(
    string Name,
    string? ExternalAccountId,
    string? AccessToken,
    IReadOnlyList<string>? OwnedNumbers,
    DateTime? SyncFromUtc = null);

public sealed record UpdateCrmTelephonyProviderAccountRequest(
    string Name,
    string? ExternalAccountId,
    string? AccessToken,
    IReadOnlyList<string>? OwnedNumbers,
    bool IsEnabled,
    DateTime? SyncFromUtc = null);

public sealed record CrmTelephonyProviderAccountReceiverDto(
    CrmTelephonyProviderAccountDto Account,
    string CallbackUrl,
    string WebhookSecret,
    string WebhookSecretHeader);

public sealed record UpdateCrmTelephonyProviderAccountBindingRequest(
    string UserId,
    string ProviderUserKey);

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
    DateTime? StatusCheckedAtUtc,
    string? RegistrationDetail = null,
    string AccountKey = "default",
    string Name = "Основная линия",
    string Mode = CrmSipAccountModes.Shared,
    string? AssignedUserId = null,
    string? AssignedUserName = null,
    string? OutboundCallerId = null,
    string? InternalNumber = null);

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
    string Password,
    IReadOnlyList<string>? IceServerUrls = null,
    string? IceUsername = null,
    string? IceCredential = null);

public sealed record UpdateCrmTelephonyBindingRequest(
    string UserId,
    string ProviderUserKey,
    string? OutboundProvider = null);

public sealed record UpdateCrmTelephonyEnabledRequest(bool IsEnabled);

public sealed record UpdateCrmTelephonyOfficeOutboundRequest(string OutboundProvider);

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
    bool UseForOutbound,
    string? Name = null,
    string Mode = CrmSipAccountModes.Shared,
    string? OutboundCallerId = null,
    string? InternalNumber = null);

public sealed record SipoutCallWebhookPayload(
    string ExternalCallId,
    string? CallerPhone,
    string? CalledPhone,
    string? CallType,
    string? ProviderUserKey,
    string? LastCaller,
    string? StartedAt,
    string? DurationSeconds,
    string? RecordingUrl,
    string? Disposition = null,
    string? DialStatus = null,
    string? HangupCause = null);

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
    string? DurationSeconds,
    string? Disposition = null,
    string? DialStatus = null,
    string? HangupCause = null);

public enum AsteriskInboundRouteOutcome
{
    Resolved,
    Unauthorized,
    Invalid
}

/// <summary>
/// Inbound callback routing. A known CRM card is routed exclusively to its
/// current responsible manager. An unknown caller on a personal provider line
/// is routed exclusively to the line owner; shared lines may use fallbacks.
/// </summary>
public sealed record AsteriskInboundRouteResult(
    AsteriskInboundRouteOutcome Outcome,
    string? PreferredExtension = null,
    IReadOnlyList<string>? FallbackExtensions = null,
    DateTime? AffinityExpiresAtUtc = null,
    bool IsExclusive = false,
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

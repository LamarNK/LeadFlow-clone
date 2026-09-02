namespace Orbita.Contracts;

public sealed record PanelUserPresenceHourSeriesDto(
    IReadOnlyList<int> TypicalByHour,
    IReadOnlyList<int> TodayByHour,
    int TypicalPeakHour,
    int TypicalPeakValue,
    int TodayPeakHour,
    int TodayPeakValue,
    int SampleDayCount,
    int CurrentHour,
    DateTime GeneratedAtUtc);

public sealed record PanelUserDto(
    string Id,
    string Email,
    bool EmailConfirmed,
    string Role,
    bool IsLocked,
    Guid? OfficeId = null,
    string? OfficeName = null,
    string? FullName = null,
    IReadOnlyList<string>? PermissionOverride = null,
    DateTime? LastSeenAtUtc = null,
    bool IsOnline = false);

public sealed record CreatePanelUserRequest(
    string Email,
    string Password,
    string Role,
    Guid? OfficeId = null,
    string? FullName = null);

public sealed record UpdatePanelUserOfficeRequest(Guid? OfficeId);

public sealed record UpdatePanelUserFullNameRequest(string FullName);

public sealed record ResetPanelUserPasswordRequest(string Password);

public sealed record UpdatePanelUserRoleRequest(string Role);

public sealed record UpdatePanelUserPermissionsRequest(
    bool UseProfilePermissions,
    IReadOnlyList<string> Permissions);

public sealed record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);

public sealed record AccessProfileDto(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Permissions);

public sealed record UpdateAccessProfileRequest(IReadOnlyList<string> Permissions);

public sealed record ServiceLogEntryDto(
    DateTime TimestampUtc,
    string Level,
    string Service,
    string Source,
    string Message,
    string? TraceId,
    bool IsTampered);

public sealed record ServiceLogsPageDto(
    IReadOnlyList<ServiceLogEntryDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record AdminWorkerListItemDto(
    Guid Id,
    string DisplayName,
    string MachineName,
    string AppVersion,
    bool IsEnabled,
    bool IsOnline,
    DateTime? LastSeenAtUtc,
    DateTime CreatedAtUtc,
    DateTime? ApiKeyRotatedAtUtc,
    bool UpdateAvailable = false,
    string? LatestReleaseVersion = null,
    Guid OfficeId = default,
    string OfficeName = "",
    bool IsMonitoringPaused = false);

public sealed record UpdateAdminWorkerRequest(string DisplayName);

public sealed record RotateWorkerApiKeyResponse(Guid WorkerId, string ApiKey);

public sealed record WorkerRegistrationInfoDto(
    bool IsConfigured,
    string MaskedSecret,
    string Source);

public sealed record PanelAuditEntryDto(
    long Id,
    DateTime TimestampUtc,
    string? ActorEmail,
    string Action,
    string? TargetType,
    string? TargetId,
    string? Details,
    string? IpAddress);

public sealed record PanelAuditPageDto(
    IReadOnlyList<PanelAuditEntryDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record PasswordPolicyDto(
    int RequiredLength,
    bool RequireDigit,
    bool RequireLowercase,
    bool RequireUppercase,
    bool RequireNonAlphanumeric,
    int RequiredUniqueChars);

public sealed record PanelProfileDto(
    string Email,
    string Role,
    Guid? OfficeId = null,
    string? OfficeName = null,
    string? FullName = null);

public static class PanelAuditActions
{
    public const string UserCreated = "user.created";
    public const string UserDeleted = "user.deleted";
    public const string UserRoleUpdated = "user.role_updated";
    public const string UserPermissionsUpdated = "user.permissions_updated";
    public const string UserFullNameUpdated = "user.full_name_updated";
    public const string UserPasswordReset = "user.password_reset";
    public const string UserPasswordChanged = "user.password_changed";
    public const string AccessProfileUpdated = "access_profile.updated";
    public const string UserLocked = "user.locked";
    public const string UserUnlocked = "user.unlocked";
    public const string UserSessionsRevoked = "user.sessions_revoked";
    public const string LoginSucceeded = "auth.login_succeeded";
    public const string LoginFailed = "auth.login_failed";
    public const string WorkerRenamed = "worker.renamed";
    public const string WorkerDisabled = "worker.disabled";
    public const string WorkerEnabled = "worker.enabled";
    public const string WorkerMonitoringPaused = "worker.monitoring_paused";
    public const string WorkerMonitoringResumed = "worker.monitoring_resumed";
    public const string WorkersMonitoringDisabledAll = "workers.monitoring_disabled_all";
    public const string WorkersMonitoringEnabledAll = "workers.monitoring_enabled_all";
    public const string WorkerKeyRotated = "worker.key_rotated";
    public const string WorkerCreated = "worker.created";
    public const string WorkerDeleted = "worker.deleted";
    public const string CrmCardDeleted = "crm.card_deleted";
    public const string BitrixWebhookUpdated = "bitrix.webhook_updated";
    public const string BitrixWebhookValidated = "bitrix.webhook_validated";
    public const string LeadFlowImportExecuted = "leadflow.import_executed";
}

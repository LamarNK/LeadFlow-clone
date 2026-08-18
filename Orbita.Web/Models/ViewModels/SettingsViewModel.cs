using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed record SettingsIndexViewModel
{
    public PageHeaderViewModel? Header { get; init; }

    public required string ActiveTab { get; init; }
    public required IReadOnlyList<SettingsTabViewModel> Tabs { get; init; }
    public IReadOnlyList<PanelUserRowViewModel> Users { get; init; } = [];
    public IReadOnlyList<PanelUserGroupViewModel> UserGroups { get; init; } = [];
    public IReadOnlyList<AccessProfileRowViewModel> Profiles { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> ProfileOptions { get; init; } = [];
    public WorkersSettingsViewModel? Workers { get; init; }
    public ProfileSettingsViewModel? Profile { get; init; }
    public ServiceLogsViewModel? Logs { get; init; }
    public WorkerReleasesSettingsViewModel? WorkerReleases { get; init; }
    public OfficesSettingsViewModel? Offices { get; init; }
    public IReadOnlyList<EventFilterOptionViewModel> OfficeOptions { get; init; } = [];
    public string? StatusMessage { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class SettingsTabViewModel
{
    public required string Id { get; init; }
    public required string Label { get; init; }
}

public sealed class PanelUserRowViewModel
{
    public required string Id { get; init; }
    public string? FullName { get; init; }
    public required string Email { get; init; }
    public required string Role { get; init; }
    public required string RoleLabel { get; init; }
    public required string ProfileId { get; init; }
    public bool IsCurrentUser { get; init; }
    public bool IsLocked { get; init; }
    public bool HasPermissionOverride { get; init; }
    public DateTime? LastSeenAtUtc { get; init; }
    public bool IsOnline { get; init; }
    public IReadOnlyList<string> EffectivePermissions { get; init; } = [];
    public string BitrixStatus { get; init; } = BitrixValidationStatuses.NotConfigured;
    public string BitrixStatusLabel { get; init; } = "Не настроено";
    public string BitrixStatusTone { get; init; } = "neutral";
    public Guid? OfficeId { get; init; }
    public string? OfficeName { get; init; }
}

public sealed class PanelUserGroupViewModel
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string Kind { get; init; } = "office";
    public string? OfficeId { get; init; }
    public IReadOnlyList<PanelUserRowViewModel> Users { get; init; } = [];
}

public sealed class OfficesSettingsViewModel
{
    public IReadOnlyList<OfficeRowViewModel> Rows { get; init; } = [];
    public OfficeDetailViewModel? Selected { get; init; }
}

public sealed class OfficeRowViewModel
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool IsEnabled { get; init; }
    public int WorkerCount { get; init; }
    public int UserCount { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class OfficeDetailViewModel
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool IsEnabled { get; init; }
    public bool BitrixTransmissionEnabled { get; init; } = true;
    /// <summary>Офис принимает отклики в CRM от операторов любого офиса.</summary>
    public bool CrmEnabled { get; init; }
    public bool RegistrationConfigured { get; init; }
    public required string MaskedRegistrationSecret { get; init; }
    public string BitrixValidationStatus { get; init; } = BitrixValidationStatuses.NotConfigured;
    public string BitrixValidationStatusLabel { get; init; } = "Не настроено";
    public string BitrixValidationStatusTone { get; init; } = "neutral";
    public string? BitrixValidationMessage { get; init; }
    public string? MaskedBitrixWebhookUrl { get; init; }
    public string? BitrixPortalHost { get; init; }
    public DateTime? BitrixLastValidatedAtUtc { get; init; }
}

public sealed class CreateOfficeFormModel
{
    public string Name { get; set; } = string.Empty;
}

public sealed class UpdateOfficeFormModel
{
    public Guid OfficeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool CrmEnabled { get; set; }
}

public sealed class UpdatePanelUserOfficeFormModel
{
    public string UserId { get; set; } = string.Empty;
    public Guid? OfficeId { get; set; }
}

public sealed class AccessProfileRowViewModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public int UsersCount { get; init; }
    public IReadOnlyList<ProfileMemberViewModel> Members { get; init; } = [];
}

public sealed class ProfileMemberViewModel
{
    public required string Id { get; init; }
    public required string Email { get; init; }
}

public sealed class WorkersSettingsViewModel
{
    public IReadOnlyList<AdminWorkerRowViewModel> Rows { get; init; } = [];
    public WorkerRegistrationViewModel? Registration { get; init; }
}

public sealed class AdminWorkerRowViewModel
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public required string MachineName { get; init; }
    public required string AppVersion { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsOnline { get; init; }
    public DateTime? LastSeenAtUtc { get; init; }
    public DateTime? ApiKeyRotatedAtUtc { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? LatestReleaseVersion { get; init; }
    public Guid OfficeId { get; init; }
    public string OfficeName { get; init; } = string.Empty;
}

public sealed class WorkerReleasesSettingsViewModel
{
    public WorkerReleaseInfoViewModel? Latest { get; init; }
    public IReadOnlyList<WorkerReleaseInfoViewModel> Versions { get; init; } = [];
}

public sealed class WorkerReleaseInfoViewModel
{
    public required string Version { get; init; }
    public string? ReleaseNotes { get; init; }
    public long FileSize { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public bool IsLatest { get; init; }
    public DateTime UploadedAtUtc { get; init; }
    public string FileSizeLabel { get; init; } = string.Empty;
}

public sealed class WorkerRegistrationViewModel
{
    public bool IsConfigured { get; init; }
    public required string MaskedSecret { get; init; }
    public required string Source { get; init; }
}

public sealed class ProfileSettingsViewModel
{
    public required string Email { get; init; }
    /// <summary>Raw role id (<see cref="PanelRoles"/>).</summary>
    public string Role { get; init; } = PanelRoles.Operator;
    public required string RoleLabel { get; init; }
    public PasswordPolicyViewModel? PasswordPolicy { get; init; }
}

public sealed class PasswordPolicyViewModel
{
    public int RequiredLength { get; init; }
    public bool RequireDigit { get; init; }
    public bool RequireLowercase { get; init; }
    public bool RequireUppercase { get; init; }
    public bool RequireNonAlphanumeric { get; init; }
    public int RequiredUniqueChars { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class ServiceLogsViewModel
{
    public string? SearchQuery { get; init; }
    public string? Level { get; init; }
    public string? Service { get; init; }
    public DateTime? Date { get; init; }
    public Guid? WorkerId { get; init; }
    public bool IsWorkerLogs { get; init; }
    public IReadOnlyList<EventFilterOptionViewModel> LevelOptions { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> ServiceOptions { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> WorkerOptions { get; init; } = [];
    public IReadOnlyList<ServiceLogRowViewModel> Rows { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
}

public sealed class LogFeedPanelViewModel
{
    public IReadOnlyList<ServiceLogRowViewModel> Rows { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
    public string EmptyMessage { get; init; } = "Записей не найдено за выбранные условия.";
    public string FeedAriaLabel { get; init; } = "Логи";
    public bool ShowServiceTag { get; init; } = true;
}

public sealed class ServiceLogRowViewModel
{
    public required DateTime TimestampUtc { get; init; }
    public required string Level { get; init; }
    public required string LevelTone { get; init; }
    public required string Service { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }
    public string? TraceId { get; init; }
    public bool IsTampered { get; init; }
}

public sealed class CreatePanelUserFormModel
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = Orbita.Contracts.PanelRoles.Operator;
    public Guid? OfficeId { get; set; }
}

public sealed class UpdatePanelUserFullNameFormModel
{
    public string UserId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
}

public sealed class UpdatePanelUserFormModel
{
    public string UserId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string OriginalFullName { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string? OriginalRole { get; set; }
    public Guid? OfficeId { get; set; }
    public Guid? OriginalOfficeId { get; set; }
    public string? Password { get; set; }
    public bool UseProfilePermissions { get; set; }
    public bool OriginalUseProfilePermissions { get; set; }
    public string? OriginalPermissionKeys { get; set; }
    public List<string> Permissions { get; set; } = [];
}

public sealed class UpdateAccessProfileFormModel
{
    public string ProfileId { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = [];
}

public sealed class ResetPanelUserPasswordFormModel
{
    public string UserId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class UpdatePanelUserRoleFormModel
{
    public string UserId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public sealed class RenameAdminWorkerFormModel
{
    public Guid WorkerId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class BitrixWorkforceEditorViewModel
{
    public required Guid OfficeId { get; init; }
    public required Guid BitrixInstanceId { get; init; }
    public string BitrixInstanceLabel { get; init; } = string.Empty;
    public required BitrixWorkforceSettingsDto Settings { get; init; }
    public string ManagerUserIdsText { get; init; } = string.Empty;
    public IReadOnlyList<BitrixWorkforceStageRuleFormModel> StageRules { get; init; } = [];
    public IReadOnlyList<BitrixWorkforceAssignmentDto> RecentAssignments { get; init; } = [];
}

public sealed class SaveBitrixWorkforceFormModel
{
    public Guid OfficeId { get; set; }
    public Guid BitrixInstanceId { get; set; }
    public string OperationMode { get; set; } = BitrixWorkforceDistribution.DisabledMode;
    public int DealCategoryId { get; set; }
    public string TimeZoneId { get; set; } = "Europe/Moscow";
    public string ManagerUserIdsText { get; set; } = string.Empty;
    public List<BitrixWorkforceStageRuleFormModel> StageRules { get; set; } = [];
    public int MorningWindowStartMinutes { get; set; } = 480;
    public int MorningWindowEndMinutes { get; set; } = 660;
    public int LateJoinReserveMinutes { get; set; } = 120;
    public decimal SingleManagerInitialReleasePercent { get; set; } = 50m;
    public int RetryDelaySeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 20;
    public bool PreserveManualNewOwner { get; set; } = true;
    public bool SyncContactOwner { get; set; } = true;
    public bool FillOnlyEmptyAvitoFields { get; set; } = true;
    public bool WriterRulesConfirmed { get; set; }
}

public sealed class BitrixWorkforceStageRuleFormModel
{
    public Guid? Id { get; set; }
    public string Scenario { get; set; } = string.Empty;
    public string SourceStageId { get; set; } = string.Empty;
    public string TargetStageId { get; set; } = string.Empty;
    public bool UsesMorningWindow { get; set; }
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; }
}

public sealed class ConfigureBitrixWorkforceReceiverFormModel
{
    public Guid OfficeId { get; set; }
    public Guid BitrixInstanceId { get; set; }
    public string ApplicationToken { get; set; } = string.Empty;
    public string? ExpectedMemberId { get; set; }
}

public sealed class BitrixIntegrationRowViewModel
{
    public required Guid OfficeId { get; init; }
    public required string OfficeName { get; init; }
    public string? PortalHost { get; init; }
    public required string ValidationStatus { get; init; }
    public required string ValidationStatusLabel { get; init; }
    public required string ValidationStatusTone { get; init; }
}

public sealed class SaveOfficeBitrixIntegrationFormModel
{
    public Guid OfficeId { get; set; }
    public string WebhookUrl { get; set; } = string.Empty;
}

public sealed class ChangeOwnPasswordFormModel
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

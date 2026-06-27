namespace Orbita.Web.Models.ViewModels;

public sealed record SettingsIndexViewModel
{
    public required string ActiveTab { get; init; }
    public required IReadOnlyList<SettingsTabViewModel> Tabs { get; init; }
    public IReadOnlyList<PanelUserRowViewModel> Users { get; init; } = [];
    public IReadOnlyList<AccessProfileRowViewModel> Profiles { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> ProfileOptions { get; init; } = [];
    public WorkersSettingsViewModel? Workers { get; init; }
    public PanelAuditViewModel? Audit { get; init; }
    public ProfileSettingsViewModel? Profile { get; init; }
    public ServiceLogsViewModel? Logs { get; init; }
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
    public required string Email { get; init; }
    public required string Role { get; init; }
    public required string RoleLabel { get; init; }
    public required string ProfileId { get; init; }
    public bool IsCurrentUser { get; init; }
    public bool IsLocked { get; init; }
}

public sealed class AccessProfileRowViewModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string PermissionsLabel { get; init; }
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
}

public sealed class WorkerRegistrationViewModel
{
    public bool IsConfigured { get; init; }
    public required string MaskedSecret { get; init; }
    public required string Source { get; init; }
}

public sealed class PanelAuditViewModel
{
    public string? SearchQuery { get; init; }
    public string? Action { get; init; }
    public DateTime? Date { get; init; }
    public IReadOnlyList<EventFilterOptionViewModel> ActionOptions { get; init; } = [];
    public IReadOnlyList<PanelAuditRowViewModel> Rows { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
}

public sealed class PanelAuditRowViewModel
{
    public required DateTime TimestampUtc { get; init; }
    public string? ActorEmail { get; init; }
    public required string Action { get; init; }
    public required string ActionLabel { get; init; }
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }
    public string? Details { get; init; }
    public string? IpAddress { get; init; }
}

public sealed class ProfileSettingsViewModel
{
    public required string Email { get; init; }
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
    public IReadOnlyList<EventFilterOptionViewModel> LevelOptions { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> ServiceOptions { get; init; } = [];
    public IReadOnlyList<ServiceLogRowViewModel> Rows { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
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
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = Orbita.Contracts.PanelRoles.Operator;
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

public sealed class ChangeOwnPasswordFormModel
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}
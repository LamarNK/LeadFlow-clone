using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class SettingsIndexBuilder
{
    public const int LogsPageSize = 50;
    public const int AuditPageSize = 50;

    public static readonly IReadOnlyList<SettingsTabViewModel> Tabs =
    [
        new() { Id = "users", Label = "Пользователи" },
        new() { Id = "profiles", Label = "Профили" },
        new() { Id = "workers", Label = "Воркеры" },
        new() { Id = "audit", Label = "Аудит" },
        new() { Id = "logs", Label = "Логи сервиса" },
        new() { Id = "profile", Label = "Мой профиль" }
    ];

    public static readonly IReadOnlyList<EventFilterOptionViewModel> ProfileOptions =
    [
        new() { Value = PanelRoles.Operator, Label = "Оператор" },
        new() { Value = PanelRoles.Admin, Label = "Администратор" }
    ];

    private static readonly IReadOnlyList<AccessProfileDto> AccessProfiles =
    [
        new(
            "admin",
            "Администратор",
            "Полный доступ к панели, настройкам и управлению пользователями.",
            ["Панель управления", "Воркеры", "Аккаунты", "События", "Ошибки", "Настройки"]),
        new(
            "operator",
            "Оператор",
            "Просмотр мониторинга без доступа к настройкам и управлению пользователями.",
            ["Панель управления", "Воркеры", "Аккаунты", "События", "Ошибки"])
    ];

    public static SettingsIndexViewModel BuildUsersTab(
        IReadOnlyList<PanelUserDto> users,
        string? currentUserId,
        string? statusMessage = null,
        string? errorMessage = null) =>
        Build(users, "users", currentUserId, statusMessage, errorMessage);

    public static SettingsIndexViewModel BuildProfilesTab(
        IReadOnlyList<PanelUserDto> users,
        string? currentUserId = null) =>
        Build(users, "profiles", currentUserId);

    public static SettingsIndexViewModel BuildWorkersTab(
        IReadOnlyList<AdminWorkerListItemDto> workers,
        WorkerRegistrationInfoDto? registration) =>
        new()
        {
            ActiveTab = "workers",
            Tabs = Tabs,
            ProfileOptions = ProfileOptions,
            Workers = new WorkersSettingsViewModel
            {
                Rows = workers.Select(MapWorker).ToList(),
                Registration = registration is null
                    ? null
                    : new WorkerRegistrationViewModel
                    {
                        IsConfigured = registration.IsConfigured,
                        MaskedSecret = registration.MaskedSecret,
                        Source = registration.Source
                    }
            }
        };

    public static SettingsIndexViewModel BuildAuditTab(
        string? q,
        string? action,
        DateTime? date,
        PanelAuditPageDto page) =>
        new()
        {
            ActiveTab = "audit",
            Tabs = Tabs,
            ProfileOptions = ProfileOptions,
            Audit = new PanelAuditViewModel
            {
                SearchQuery = q,
                Action = action,
                Date = date ?? DateTime.UtcNow.Date,
                ActionOptions = BuildAuditActionOptions(),
                Rows = page.Items.Select(MapAuditRow).ToList(),
                Pagination = new PaginationViewModel
                {
                    Page = page.Page,
                    PageSize = page.PageSize,
                    TotalItems = page.Total
                }
            }
        };

    public static SettingsIndexViewModel BuildProfileTab(
        PanelProfileDto profile,
        PasswordPolicyDto? policy) =>
        new()
        {
            ActiveTab = "profile",
            Tabs = Tabs,
            ProfileOptions = ProfileOptions,
            Profile = new ProfileSettingsViewModel
            {
                Email = profile.Email,
                RoleLabel = RoleLabel(PanelRoles.Normalize(profile.Role)),
                PasswordPolicy = policy is null ? null : MapPasswordPolicy(policy)
            }
        };

    public static SettingsIndexViewModel BuildLogsTab(
        string? q,
        string? level,
        string? service,
        DateTime? date,
        ServiceLogsPageDto page) =>
        new()
        {
            ActiveTab = "logs",
            Tabs = Tabs,
            ProfileOptions = ProfileOptions,
            Logs = new ServiceLogsViewModel
            {
                SearchQuery = q,
                Level = level,
                Service = service,
                Date = date ?? DateTime.UtcNow.Date,
                LevelOptions = BuildLevelOptions(),
                ServiceOptions = BuildServiceOptions(),
                Rows = page.Items.Select(MapLogRow).ToList(),
                Pagination = new PaginationViewModel
                {
                    Page = page.Page,
                    PageSize = page.PageSize,
                    TotalItems = page.Total
                }
            }
        };

    private static SettingsIndexViewModel Build(
        IReadOnlyList<PanelUserDto> users,
        string activeTab,
        string? currentUserId,
        string? statusMessage = null,
        string? errorMessage = null) =>
        new()
        {
            ActiveTab = activeTab,
            Tabs = Tabs,
            Users = users.Select(u => MapUser(u, currentUserId)).ToList(),
            Profiles = BuildProfiles(users),
            ProfileOptions = ProfileOptions,
            StatusMessage = statusMessage,
            ErrorMessage = errorMessage
        };

    private static PanelUserRowViewModel MapUser(PanelUserDto user, string? currentUserId)
    {
        var role = PanelRoles.Normalize(user.Role);
        return new PanelUserRowViewModel
        {
            Id = user.Id,
            Email = user.Email,
            Role = role,
            RoleLabel = RoleLabel(role),
            ProfileId = PanelRoles.ProfileIdForRole(role),
            IsCurrentUser = string.Equals(user.Id, currentUserId, StringComparison.Ordinal),
            IsLocked = user.IsLocked
        };
    }

    private static AdminWorkerRowViewModel MapWorker(AdminWorkerListItemDto worker) =>
        new()
        {
            Id = worker.Id,
            DisplayName = worker.DisplayName,
            MachineName = worker.MachineName,
            AppVersion = worker.AppVersion,
            IsEnabled = worker.IsEnabled,
            IsOnline = worker.IsOnline,
            LastSeenAtUtc = worker.LastSeenAtUtc,
            ApiKeyRotatedAtUtc = worker.ApiKeyRotatedAtUtc
        };

    private static PanelAuditRowViewModel MapAuditRow(PanelAuditEntryDto entry) =>
        new()
        {
            TimestampUtc = entry.TimestampUtc,
            ActorEmail = entry.ActorEmail,
            Action = entry.Action,
            ActionLabel = AuditActionLabel(entry.Action),
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            Details = entry.Details,
            IpAddress = entry.IpAddress
        };

    private static PasswordPolicyViewModel MapPasswordPolicy(PasswordPolicyDto policy)
    {
        var parts = new List<string> { $"минимум {policy.RequiredLength} символов" };
        if (policy.RequireDigit)
        {
            parts.Add("цифра");
        }

        if (policy.RequireLowercase)
        {
            parts.Add("строчная буква");
        }

        if (policy.RequireUppercase)
        {
            parts.Add("заглавная буква");
        }

        if (policy.RequireNonAlphanumeric)
        {
            parts.Add("спецсимвол");
        }

        if (policy.RequiredUniqueChars > 1)
        {
            parts.Add($"{policy.RequiredUniqueChars} уникальных символов");
        }

        return new PasswordPolicyViewModel
        {
            RequiredLength = policy.RequiredLength,
            RequireDigit = policy.RequireDigit,
            RequireLowercase = policy.RequireLowercase,
            RequireUppercase = policy.RequireUppercase,
            RequireNonAlphanumeric = policy.RequireNonAlphanumeric,
            RequiredUniqueChars = policy.RequiredUniqueChars,
            Summary = string.Join(", ", parts)
        };
    }

    private static IReadOnlyList<AccessProfileRowViewModel> BuildProfiles(IReadOnlyList<PanelUserDto> users)
    {
        var mappedUsers = users
            .Select(u => new { User = u, Role = PanelRoles.Normalize(u.Role) })
            .ToList();

        return AccessProfiles
            .Select(profile =>
            {
                var role = PanelRoles.RoleForProfileId(profile.Id);
                var members = mappedUsers
                    .Where(x => x.Role == role)
                    .Select(x => new ProfileMemberViewModel { Id = x.User.Id, Email = x.User.Email })
                    .OrderBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new AccessProfileRowViewModel
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    Description = profile.Description,
                    PermissionsLabel = string.Join(" · ", profile.Permissions),
                    UsersCount = members.Count,
                    Members = members
                };
            })
            .ToList();
    }

    private static IReadOnlyList<EventFilterOptionViewModel> BuildLevelOptions() =>
    [
        new() { Value = "", Label = "Все уровни" },
        new() { Value = "Info", Label = "Info" },
        new() { Value = "Debug", Label = "Debug" },
        new() { Value = "Warning", Label = "Warning" },
        new() { Value = "Error", Label = "Error" }
    ];

    private static IReadOnlyList<EventFilterOptionViewModel> BuildServiceOptions() =>
    [
        new() { Value = "", Label = "Все сервисы" },
        new() { Value = "Orbita.Web", Label = "Orbita.Web" },
        new() { Value = "Orbita.Api", Label = "Orbita.Api" }
    ];

    private static IReadOnlyList<EventFilterOptionViewModel> BuildAuditActionOptions() =>
    [
        new() { Value = "", Label = "Все действия" },
        new() { Value = PanelAuditActions.UserCreated, Label = AuditActionLabel(PanelAuditActions.UserCreated) },
        new() { Value = PanelAuditActions.UserDeleted, Label = AuditActionLabel(PanelAuditActions.UserDeleted) },
        new() { Value = PanelAuditActions.UserRoleUpdated, Label = AuditActionLabel(PanelAuditActions.UserRoleUpdated) },
        new() { Value = PanelAuditActions.UserPasswordReset, Label = AuditActionLabel(PanelAuditActions.UserPasswordReset) },
        new() { Value = PanelAuditActions.UserPasswordChanged, Label = AuditActionLabel(PanelAuditActions.UserPasswordChanged) },
        new() { Value = PanelAuditActions.UserLocked, Label = AuditActionLabel(PanelAuditActions.UserLocked) },
        new() { Value = PanelAuditActions.UserUnlocked, Label = AuditActionLabel(PanelAuditActions.UserUnlocked) },
        new() { Value = PanelAuditActions.UserSessionsRevoked, Label = AuditActionLabel(PanelAuditActions.UserSessionsRevoked) },
        new() { Value = PanelAuditActions.LoginSucceeded, Label = AuditActionLabel(PanelAuditActions.LoginSucceeded) },
        new() { Value = PanelAuditActions.LoginFailed, Label = AuditActionLabel(PanelAuditActions.LoginFailed) },
        new() { Value = PanelAuditActions.WorkerRenamed, Label = AuditActionLabel(PanelAuditActions.WorkerRenamed) },
        new() { Value = PanelAuditActions.WorkerDisabled, Label = AuditActionLabel(PanelAuditActions.WorkerDisabled) },
        new() { Value = PanelAuditActions.WorkerEnabled, Label = AuditActionLabel(PanelAuditActions.WorkerEnabled) },
        new() { Value = PanelAuditActions.WorkerKeyRotated, Label = AuditActionLabel(PanelAuditActions.WorkerKeyRotated) }
    ];

    private static ServiceLogRowViewModel MapLogRow(ServiceLogEntryDto entry)
    {
        var tone = entry.Level switch
        {
            "Error" => "error",
            "Warning" => "warning",
            "Debug" => "info",
            _ => "success"
        };

        return new ServiceLogRowViewModel
        {
            TimestampUtc = entry.TimestampUtc,
            Level = entry.Level,
            LevelTone = tone,
            Service = entry.Service,
            Source = entry.Source,
            Message = entry.Message,
            TraceId = entry.TraceId,
            IsTampered = entry.IsTampered
        };
    }

    public static string AuditActionLabel(string action) =>
        action switch
        {
            PanelAuditActions.UserCreated => "Пользователь создан",
            PanelAuditActions.UserDeleted => "Пользователь удалён",
            PanelAuditActions.UserRoleUpdated => "Роль изменена",
            PanelAuditActions.UserPasswordReset => "Пароль сброшен",
            PanelAuditActions.UserPasswordChanged => "Пароль изменён",
            PanelAuditActions.UserLocked => "Пользователь заблокирован",
            PanelAuditActions.UserUnlocked => "Пользователь разблокирован",
            PanelAuditActions.UserSessionsRevoked => "Сессии завершены",
            PanelAuditActions.LoginSucceeded => "Вход выполнен",
            PanelAuditActions.LoginFailed => "Неудачный вход",
            PanelAuditActions.WorkerRenamed => "Воркер переименован",
            PanelAuditActions.WorkerDisabled => "Воркер отключён",
            PanelAuditActions.WorkerEnabled => "Воркер включён",
            PanelAuditActions.WorkerKeyRotated => "API-ключ перевыпущен",
            _ => action
        };

    private static string RoleLabel(string role) =>
        role == PanelRoles.Admin ? "Администратор" : "Оператор";
}
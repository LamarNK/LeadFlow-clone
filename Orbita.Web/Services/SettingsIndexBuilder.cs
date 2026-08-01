using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class SettingsIndexBuilder
{
    public const int LogsPageSize = 50;
    public const int AuditPageSize = 50;

    public static readonly IReadOnlyList<SettingsTabViewModel> Tabs =
    [
        new() { Id = "offices", Label = "Офисы" },
        new() { Id = "users", Label = "Пользователи" },
        new() { Id = "profiles", Label = "Профили" },
        new() { Id = "workers", Label = "Воркеры" },
        new() { Id = "leadflow-import", Label = "Импорт LeadFlow" },
        new() { Id = "worker-releases", Label = "Обновления воркера" },
        new() { Id = "audit", Label = "Аудит" },
        new() { Id = "logs", Label = "Логи сервиса" },
        new() { Id = "integrations", Label = "Битриксы и связи" }
    ];

    public static readonly IReadOnlyList<EventFilterOptionViewModel> ProfileOptions =
    [
        new() { Value = PanelRoles.Operator, Label = "Оператор" },
        new() { Value = PanelRoles.Manager, Label = "Менеджер" },
        new() { Value = PanelRoles.Admin, Label = "Администратор" }
    ];

    private static readonly IReadOnlyList<AccessProfileDto> AccessProfiles =
    [
        new(
            "admin",
            "Администратор",
            "Полный доступ к панели, настройкам и управлению пользователями.",
            ["Панель управления", "Воркеры", "Аккаунты", "События", "Ошибки", "Настройки", "Администрирование", "Интеграции Bitrix"]),
        new(
            "operator",
            "Оператор",
            "Просмотр мониторинга и личные настройки (профиль, Bitrix24).",
            ["Панель управления", "Воркеры", "Аккаунты", "События", "Ошибки", "Настройки"])
    ];

    public static SettingsIndexViewModel BuildOfficesTab(
        IReadOnlyList<OfficeDto> offices,
        OfficeDetailDto? selected = null,
        string? statusMessage = null,
        string? errorMessage = null) =>
        new()
        {
            ActiveTab = "offices",
            Tabs = Tabs,
            ProfileOptions = ProfileOptions,
            OfficeOptions = offices.Select(o => new EventFilterOptionViewModel
            {
                Value = o.Id.ToString(),
                Label = o.Name
            }).ToList(),
            Offices = new OfficesSettingsViewModel
            {
                Rows = offices.Select(MapOffice).ToList(),
                Selected = selected is null ? null : MapOfficeDetail(selected)
            },
            StatusMessage = statusMessage,
            ErrorMessage = errorMessage
        };

    public static SettingsIndexViewModel BuildUsersTab(
        IReadOnlyList<PanelUserDto> users,
        IReadOnlyList<OfficeDto> offices,
        string? currentUserId,
        string? statusMessage = null,
        string? errorMessage = null) =>
        Build(users, offices, "users", currentUserId, statusMessage, errorMessage);

    public static SettingsIndexViewModel BuildProfilesTab(
        IReadOnlyList<PanelUserDto> users,
        IReadOnlyList<OfficeDto> offices,
        string? currentUserId = null) =>
        Build(users, offices, "profiles", currentUserId);

    public static SettingsIndexViewModel BuildIntegrationsTab(
        IReadOnlyList<OfficeDto> offices,
        BitrixDistributionSettingsViewModel? bitrixDistribution = null) =>
        new()
        {
            ActiveTab = "integrations",
            Tabs = Tabs,
            ProfileOptions = ProfileOptions,
            OfficeOptions = offices.Select(o => new EventFilterOptionViewModel
            {
                Value = o.Id.ToString(),
                Label = o.Name
            }).ToList(),
            BitrixDistribution = bitrixDistribution ?? new BitrixDistributionSettingsViewModel
            {
                OfficeOptions = offices.Select(o => new EventFilterOptionViewModel
                {
                    Value = o.Id.ToString(),
                    Label = o.Name
                }).ToList()
            }
        };

    public static SettingsIndexViewModel BuildLeadFlowImportTab(
        IReadOnlyList<OfficeDto> offices) =>
        new()
        {
            ActiveTab = "leadflow-import",
            Tabs = Tabs,
            ProfileOptions = ProfileOptions,
            OfficeOptions = offices.Select(o => new EventFilterOptionViewModel
            {
                Value = o.Id.ToString(),
                Label = o.Name
            }).ToList(),
            LeadFlowImport = new LeadFlowImportSettingsViewModel
            {
                OfficeOptions = offices.Select(o => new EventFilterOptionViewModel
                {
                    Value = o.Id.ToString(),
                    Label = o.Name
                }).ToList()
            }
        };

    public static SettingsIndexViewModel BuildWorkerReleasesTab(WorkerReleaseListResponse releases) =>
        new()
        {
            ActiveTab = "worker-releases",
            Tabs = Tabs,
            ProfileOptions = ProfileOptions,
            WorkerReleases = new WorkerReleasesSettingsViewModel
            {
                Latest = releases.Latest is null ? null : MapRelease(releases.Latest),
                Versions = releases.Versions.Select(MapRelease).ToList()
            }
        };

    public static SettingsIndexViewModel BuildWorkersTab(
        IReadOnlyList<AdminWorkerListItemDto> workers,
        IReadOnlyList<OfficeDto> offices,
        WorkerRegistrationInfoDto? registration) =>
        new()
        {
            ActiveTab = "workers",
            Tabs = Tabs,
            ProfileOptions = ProfileOptions,
            OfficeOptions = offices.Select(o => new EventFilterOptionViewModel
            {
                Value = o.Id.ToString(),
                Label = o.Name
            }).ToList(),
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
        ServiceLogsPageDto page,
        Guid? workerId = null,
        IReadOnlyList<EventFilterOptionViewModel>? workerOptions = null) =>
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
                WorkerId = workerId,
                IsWorkerLogs = workerId.HasValue,
                LevelOptions = BuildLevelOptions(),
                ServiceOptions = BuildServiceOptions(),
                WorkerOptions = workerOptions ?? [],
                Rows = page.Items.Select(MapLogRow).ToList(),
                Pagination = new PaginationViewModel
                {
                    Page = page.Page,
                    PageSize = page.PageSize,
                    TotalItems = page.Total
                }
            }
        };

    public static SettingsIndexViewModel BuildWorkerLogsTab(
        string? q,
        string? level,
        DateTime? date,
        WorkerLogsPageDto page,
        Guid workerId,
        IReadOnlyList<EventFilterOptionViewModel> workerOptions) =>
        new()
        {
            ActiveTab = "logs",
            Tabs = Tabs,
            ProfileOptions = ProfileOptions,
            Logs = new ServiceLogsViewModel
            {
                SearchQuery = q,
                Level = level,
                Date = date ?? DateTime.UtcNow.Date,
                WorkerId = workerId,
                IsWorkerLogs = true,
                LevelOptions = BuildLevelOptions(),
                WorkerOptions = workerOptions,
                Rows = page.Items.Select(x => MapWorkerLogRow(x)).ToList(),
                Pagination = new PaginationViewModel
                {
                    Page = page.Page,
                    PageSize = page.PageSize,
                    TotalItems = page.Total
                }
            }
        };

    public static WorkerLogsPanelViewModel BuildWorkerDetailsLogsPanel(
        string? q,
        string? level,
        DateTime? date,
        int page,
        WorkerLogsPageDto pageDto) =>
        new()
        {
            SearchQuery = q,
            Level = level,
            Date = date ?? DateTime.UtcNow.Date,
            Page = page,
            LevelOptions = BuildLevelOptions(),
            Feed = BuildLogFeedPanel(
                pageDto.Items.Select(x => MapWorkerLogRow(x)).ToList(),
                pageDto.Total,
                pageDto.Page,
                pageDto.PageSize,
                "Логи воркера",
                showServiceTag: false)
        };

    public static LogFeedPanelViewModel BuildLogFeedPanel(
        IReadOnlyList<ServiceLogRowViewModel> rows,
        int total,
        int page,
        int pageSize,
        string ariaLabel,
        bool showServiceTag = true) =>
        new()
        {
            Rows = rows,
            ShowServiceTag = showServiceTag,
            FeedAriaLabel = ariaLabel,
            Pagination = new PaginationViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalItems = total
            }
        };

    private static SettingsIndexViewModel Build(
        IReadOnlyList<PanelUserDto> users,
        IReadOnlyList<OfficeDto> offices,
        string activeTab,
        string? currentUserId,
        string? statusMessage = null,
        string? errorMessage = null) =>
        new()
        {
            ActiveTab = activeTab,
            Tabs = Tabs,
            Users = users.Select(u => MapUser(u, offices, currentUserId)).ToList(),
            Profiles = BuildProfiles(users),
            ProfileOptions = ProfileOptions,
            OfficeOptions = offices.Select(o => new EventFilterOptionViewModel
            {
                Value = o.Id.ToString(),
                Label = o.Name
            }).ToList(),
            StatusMessage = statusMessage,
            ErrorMessage = errorMessage
        };

    private static PanelUserRowViewModel MapUser(
        PanelUserDto user,
        IReadOnlyList<OfficeDto> offices,
        string? currentUserId)
    {
        var role = PanelRoles.Normalize(user.Role);
        var office = user.OfficeId is Guid officeId
            ? offices.FirstOrDefault(x => x.Id == officeId)
            : null;
        var (bitrixLabel, bitrixTone) = MapValidationStatus(office?.BitrixValidationStatus);
        return new PanelUserRowViewModel
        {
            Id = user.Id,
            Email = user.Email,
            Role = role,
            RoleLabel = RoleLabel(role),
            ProfileId = PanelRoles.ProfileIdForRole(role),
            IsCurrentUser = string.Equals(user.Id, currentUserId, StringComparison.Ordinal),
            IsLocked = user.IsLocked,
            BitrixStatus = office?.BitrixValidationStatus ?? BitrixValidationStatuses.NotConfigured,
            BitrixStatusLabel = bitrixLabel,
            BitrixStatusTone = bitrixTone,
            OfficeId = user.OfficeId,
            OfficeName = user.OfficeName
        };
    }

    private static OfficeRowViewModel MapOffice(OfficeDto office) =>
        new()
        {
            Id = office.Id,
            Name = office.Name,
            IsEnabled = office.IsEnabled,
            WorkerCount = office.WorkerCount,
            UserCount = office.UserCount,
            CreatedAtUtc = office.CreatedAtUtc
        };

    private static OfficeDetailViewModel MapOfficeDetail(OfficeDetailDto office)
    {
        var (bitrixLabel, bitrixTone) = MapValidationStatus(office.BitrixValidationStatus);
        return new()
        {
            Id = office.Id,
            Name = office.Name,
            IsEnabled = office.IsEnabled,
            BitrixTransmissionEnabled = office.BitrixTransmissionEnabled,
            CrmEnabled = office.CrmEnabled,
            RegistrationConfigured = office.RegistrationConfigured,
            MaskedRegistrationSecret = office.MaskedRegistrationSecret,
            BitrixValidationStatus = office.BitrixValidationStatus,
            BitrixValidationStatusLabel = bitrixLabel,
            BitrixValidationStatusTone = bitrixTone,
            BitrixValidationMessage = office.BitrixValidationMessage,
            MaskedBitrixWebhookUrl = office.MaskedBitrixWebhookUrl,
            BitrixPortalHost = office.BitrixPortalHost,
            BitrixLastValidatedAtUtc = office.BitrixLastValidatedAtUtc
        };
    }

    private static BitrixIntegrationRowViewModel MapOfficeIntegrationRow(OfficeDto office)
    {
        var (label, tone) = MapValidationStatus(office.BitrixValidationStatus);
        return new BitrixIntegrationRowViewModel
        {
            OfficeId = office.Id,
            OfficeName = office.Name,
            PortalHost = office.BitrixPortalHost,
            ValidationStatus = office.BitrixValidationStatus,
            ValidationStatusLabel = label,
            ValidationStatusTone = tone
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
            ApiKeyRotatedAtUtc = worker.ApiKeyRotatedAtUtc,
            UpdateAvailable = worker.UpdateAvailable,
            LatestReleaseVersion = worker.LatestReleaseVersion,
            OfficeId = worker.OfficeId,
            OfficeName = worker.OfficeName
        };

    private static WorkerReleaseInfoViewModel MapRelease(WorkerReleaseInfoDto release) =>
        new()
        {
            Version = release.Version,
            ReleaseNotes = release.ReleaseNotes,
            FileSize = release.FileSize,
            Sha256 = release.Sha256,
            IsLatest = release.IsLatest,
            UploadedAtUtc = release.UploadedAtUtc,
            FileSizeLabel = FormatFileSize(release.FileSize)
        };

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var kb = bytes / 1024d;
        if (kb < 1024)
        {
            return $"{kb:0.#} KB";
        }

        var mb = kb / 1024d;
        return $"{mb:0.#} MB";
    }

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
        new() { Value = PanelAuditActions.WorkerKeyRotated, Label = AuditActionLabel(PanelAuditActions.WorkerKeyRotated) },
        new() { Value = PanelAuditActions.BitrixWebhookUpdated, Label = AuditActionLabel(PanelAuditActions.BitrixWebhookUpdated) },
        new() { Value = PanelAuditActions.BitrixWebhookValidated, Label = AuditActionLabel(PanelAuditActions.BitrixWebhookValidated) },
        new() { Value = PanelAuditActions.LeadFlowImportExecuted, Label = AuditActionLabel(PanelAuditActions.LeadFlowImportExecuted) }
    ];

    public static ServiceLogRowViewModel MapWorkerLogRow(WorkerLogEntryDto entry, string serviceLabel = "Orbita.Worker")
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
            Service = serviceLabel,
            Source = entry.Source,
            Message = entry.Message,
            TraceId = entry.TraceId,
            IsTampered = entry.IsTampered
        };
    }

    public static IReadOnlyList<EventFilterOptionViewModel> BuildWorkerOptions(
        IReadOnlyList<AdminWorkerListItemDto> workers,
        Guid? selectedWorkerId = null)
    {
        var options = new List<EventFilterOptionViewModel>
        {
            new() { Value = "", Label = "Сервисные логи" }
        };

        options.AddRange(workers
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(w => new EventFilterOptionViewModel
            {
                Value = w.Id.ToString(),
                Label = w.DisplayName
            }));

        return options;
    }

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
            PanelAuditActions.BitrixWebhookUpdated => "Вебхук Bitrix обновлён",
            PanelAuditActions.BitrixWebhookValidated => "Вебхук Bitrix проверен",
            PanelAuditActions.LeadFlowImportExecuted => "Импорт LeadFlow",
            _ => action
        };

    private static (string Label, string Tone) MapValidationStatus(string? status) =>
        status switch
        {
            BitrixValidationStatuses.Ok => ("Подключено", "success"),
            BitrixValidationStatuses.Warning => ("Ограничения", "warning"),
            BitrixValidationStatuses.Error => ("Ошибка", "error"),
            _ => ("Не настроено", "neutral")
        };

    private static string RoleLabel(string role) => role switch
    {
        PanelRoles.Admin => "Администратор",
        PanelRoles.Manager => "Менеджер",
        _ => "Оператор"
    };
}

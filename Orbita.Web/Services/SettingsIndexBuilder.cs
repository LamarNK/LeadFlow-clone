using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class SettingsIndexBuilder
{
    public const int LogsPageSize = 50;

    public static readonly IReadOnlyList<SettingsTabViewModel> Tabs =
    [
        new() { Id = "offices", Label = "Офисы" },
        new() { Id = "users", Label = "Пользователи" },
        new() { Id = "profiles", Label = "Профили" },
        new() { Id = "workers", Label = "Воркеры" },
        new() { Id = "worker-releases", Label = "Обновления воркера" },
        new() { Id = "logs", Label = "Логи сервиса" }
    ];

    public static readonly IReadOnlyList<EventFilterOptionViewModel> ProfileOptions =
    [
        new() { Value = PanelRoles.Operator, Label = PanelRoles.Label(PanelRoles.Operator) },
        new() { Value = PanelRoles.Manager, Label = PanelRoles.Label(PanelRoles.Manager) },
        new() { Value = PanelRoles.SeniorManager, Label = PanelRoles.Label(PanelRoles.SeniorManager) },
        new() { Value = PanelRoles.OfficeLead, Label = PanelRoles.Label(PanelRoles.OfficeLead) },
        new() { Value = PanelRoles.Admin, Label = PanelRoles.Label(PanelRoles.Admin) }
    ];

    public static readonly IReadOnlyList<AccessProfileDto> DefaultAccessProfiles =
        PanelPermissions.Profiles
            .Select(profile => new AccessProfileDto(
                profile.Id,
                profile.Name,
                profile.Description,
                PanelPermissions.DefaultForRole(profile.Role)))
            .ToArray();

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
        IReadOnlyList<AccessProfileDto>? accessProfiles = null,
        string? statusMessage = null,
        string? errorMessage = null,
        PanelUserPresenceHourSeriesDto? presenceHours = null) =>
        Build(users, offices, "users", currentUserId, statusMessage, errorMessage, accessProfiles, presenceHours);

    public static SettingsIndexViewModel BuildProfilesTab(
        IReadOnlyList<PanelUserDto> users,
        IReadOnlyList<OfficeDto> offices,
        IReadOnlyList<AccessProfileDto> profiles,
        string? currentUserId = null) =>
        Build(users, offices, "profiles", currentUserId, accessProfiles: profiles);

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
                RoleLabel = PanelRoles.Label(profile.Role),
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
        string? errorMessage = null,
        IReadOnlyList<AccessProfileDto>? accessProfiles = null,
        PanelUserPresenceHourSeriesDto? presenceHours = null)
    {
        var profiles = accessProfiles ?? DefaultAccessProfiles;
        var mappedUsers = users.Select(u => MapUser(u, offices, currentUserId, profiles)).ToList();
        return new SettingsIndexViewModel
        {
            ActiveTab = activeTab,
            Tabs = Tabs,
            Users = mappedUsers,
            UserGroups = BuildUserGroups(mappedUsers, offices),
            PresenceStats = activeTab == "users" ? BuildPresenceStats(mappedUsers, presenceHours) : null,
            Profiles = BuildProfiles(users, profiles),
            ProfileOptions = ProfileOptions,
            OfficeOptions = offices.Select(o => new EventFilterOptionViewModel
            {
                Value = o.Id.ToString(),
                Label = o.Name
            }).ToList(),
            StatusMessage = statusMessage,
            ErrorMessage = errorMessage
        };
    }

    internal static PanelUserPresenceStatsViewModel BuildPresenceStats(
        IReadOnlyList<PanelUserRowViewModel> users,
        PanelUserPresenceHourSeriesDto? presenceHours,
        DateTime? nowUtc = null)
    {
        var online = users.Count(x => x.IsOnline);
        var neverSeen = users.Count(x => x.LastSeenAtUtc is null);
        var series = presenceHours ?? new PanelUserPresenceHourSeriesDto(
            new int[24],
            new int[24],
            0,
            0,
            0,
            0,
            0,
            0,
            nowUtc ?? DateTime.UtcNow);
        var typical = PadHours(series.TypicalByHour);
        var today = PadHours(series.TodayByHour);
        var maxBar = Math.Max(1, typical.Concat(today).Max());
        var currentHour = Math.Clamp(series.CurrentHour, 0, 23);

        var hours = new List<PanelUserPresenceHourBarViewModel>(24);
        for (var hour = 0; hour < 24; hour++)
        {
            var typicalValue = typical[hour];
            var todayValue = today[hour];
            var heightSource = Math.Max(typicalValue, todayValue);
            hours.Add(new PanelUserPresenceHourBarViewModel
            {
                Hour = hour,
                Typical = typicalValue,
                Today = todayValue,
                HeightPercent = heightSource == 0 ? 0 : Math.Max(8, (int)Math.Round(heightSource * 100d / maxBar)),
                IsTypicalPeak = series.TypicalPeakValue > 0 && hour == series.TypicalPeakHour,
                IsCurrentHour = hour == currentHour,
                AxisLabel = hour % 6 == 0 ? hour.ToString("00") : string.Empty,
                Title = BuildHourTooltip(hour, typicalValue, todayValue)
            });
        }

        return new PanelUserPresenceStatsViewModel
        {
            Total = users.Count,
            Online = online,
            Offline = Math.Max(0, users.Count - online - neverSeen),
            NeverSeen = neverSeen,
            CurrentHour = currentHour,
            TypicalPeakLabel = series.TypicalPeakValue > 0 ? FormatHourRange(series.TypicalPeakHour) : null,
            TodayPeakLabel = series.TodayPeakValue > 0 ? FormatHourRange(series.TodayPeakHour) : null,
            TodayPeakValue = series.TodayPeakValue,
            HasHourlyData = typical.Any(v => v > 0) || today.Any(v => v > 0),
            Hours = hours
        };
    }

    internal static string FormatHourRange(int hour)
    {
        var start = ((hour % 24) + 24) % 24;
        var end = (start + 1) % 24;
        return $"{start:00}:00–{end:00}:00";
    }

    private static int[] PadHours(IReadOnlyList<int>? values)
    {
        var hours = new int[24];
        if (values is null)
        {
            return hours;
        }

        for (var i = 0; i < Math.Min(24, values.Count); i++)
        {
            hours[i] = Math.Max(0, values[i]);
        }

        return hours;
    }

    private static string BuildHourTooltip(int hour, int typical, int today)
    {
        var range = FormatHourRange(hour);
        if (typical <= 0 && today <= 0)
        {
            return $"{range} · нет активности";
        }

        return $"{range} · обычно {typical} чел. · сегодня {today} чел.";
    }

    internal static IReadOnlyList<PanelUserGroupViewModel> BuildUserGroups(
        IReadOnlyList<PanelUserRowViewModel> users,
        IReadOnlyList<OfficeDto> offices)
    {
        static int RoleOrder(string role) => role switch
        {
            PanelRoles.Admin => 0,
            PanelRoles.OfficeLead => 1,
            PanelRoles.SeniorManager => 2,
            PanelRoles.Manager => 3,
            _ => 4
        };

        static IReadOnlyList<PanelUserRowViewModel> SortUsers(IEnumerable<PanelUserRowViewModel> source) =>
            source
                .OrderBy(x => RoleOrder(x.Role))
                .ThenBy(x => string.IsNullOrWhiteSpace(x.FullName) ? x.Email : x.FullName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
                .ToList();

        var groups = new List<PanelUserGroupViewModel>();

        var admins = SortUsers(users.Where(x => x.Role == PanelRoles.Admin));
        if (admins.Count > 0)
        {
            groups.Add(new PanelUserGroupViewModel
            {
                Key = "admins",
                Title = "Администраторы",
                Subtitle = "Доступ ко всем офисам",
                Kind = "admins",
                Users = admins
            });
        }

        var nonAdmins = users.Where(x => x.Role != PanelRoles.Admin).ToList();
        var officeOrder = offices
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var office in officeOrder)
        {
            var officeUsers = SortUsers(nonAdmins.Where(x => x.OfficeId == office.Id));
            if (officeUsers.Count == 0)
            {
                continue;
            }

            groups.Add(new PanelUserGroupViewModel
            {
                Key = $"office:{office.Id:D}",
                Title = office.Name,
                Subtitle = "Офис",
                Kind = "office",
                OfficeId = office.Id.ToString("D"),
                Users = officeUsers
            });
        }

        var unassigned = SortUsers(nonAdmins.Where(x => x.OfficeId is null));
        if (unassigned.Count > 0)
        {
            groups.Add(new PanelUserGroupViewModel
            {
                Key = "unassigned",
                Title = "Без офиса",
                Subtitle = "Нужно назначить офис",
                Kind = "unassigned",
                Users = unassigned
            });
        }

        return groups;
    }

    private static PanelUserRowViewModel MapUser(
        PanelUserDto user,
        IReadOnlyList<OfficeDto> offices,
        string? currentUserId,
        IReadOnlyList<AccessProfileDto> profiles)
    {
        var role = PanelRoles.Normalize(user.Role);
        var profilePermissions = profiles
            .FirstOrDefault(profile => profile.Id == PanelRoles.ProfileIdForRole(role))
            ?.Permissions
            ?? PanelPermissions.DefaultForRole(role);
        var office = user.OfficeId is Guid officeId
            ? offices.FirstOrDefault(x => x.Id == officeId)
            : null;
        var (bitrixLabel, bitrixTone) = MapValidationStatus(office?.BitrixValidationStatus);
        return new PanelUserRowViewModel
        {
            Id = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Role = role,
            RoleLabel = PanelRoles.Label(role),
            ProfileId = PanelRoles.ProfileIdForRole(role),
            IsCurrentUser = string.Equals(user.Id, currentUserId, StringComparison.Ordinal),
            IsLocked = user.IsLocked,
            HasPermissionOverride = user.PermissionOverride is not null,
            LastSeenAtUtc = user.LastSeenAtUtc,
            IsOnline = user.IsOnline,
            EffectivePermissions = user.PermissionOverride ?? profilePermissions,
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

    private static IReadOnlyList<AccessProfileRowViewModel> BuildProfiles(
        IReadOnlyList<PanelUserDto> users,
        IReadOnlyList<AccessProfileDto> profiles)
    {
        var mappedUsers = users
            .Select(u => new { User = u, Role = PanelRoles.Normalize(u.Role) })
            .ToList();

        return profiles
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
                    Permissions = profile.Permissions,
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
        new() { Value = "Orbita.Api", Label = "Orbita.Api" },
        new() { Value = "Orbita.Worker", Label = "Orbita.Worker" }
    ];

    public static IReadOnlyList<EventFilterOptionViewModel> BuildWorkerOptions(
        IReadOnlyList<AdminWorkerListItemDto> workers,
        Guid? selectedWorkerId = null)
    {
        var options = new List<EventFilterOptionViewModel>
        {
            new() { Value = string.Empty, Label = "Сервисные логи" }
        };

        options.AddRange(workers
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(worker => new EventFilterOptionViewModel
            {
                Value = worker.Id.ToString("D"),
                Label = worker.DisplayName
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

    private static (string Label, string Tone) MapValidationStatus(string? status) =>
        status switch
        {
            BitrixValidationStatuses.Ok => ("Подключено", "success"),
            BitrixValidationStatuses.Warning => ("Ограничения", "warning"),
            BitrixValidationStatuses.Error => ("Ошибка", "error"),
            _ => ("Не настроено", "neutral")
        };

}

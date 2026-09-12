namespace Orbita.Contracts;

public sealed record PanelPermissionDefinition(string Id, string Label, string Description);
public sealed record PanelAccessProfileDefinition(string Id, string Role, string Name, string Description);

public static class PanelPermissions
{
    public const string ClaimType = "orbita.permission";
    public const string Dashboard = "dashboard";
    public const string Workers = "workers";
    public const string Accounts = "accounts";
    public const string Balances = "balances";
    public const string Listings = "listings";
    public const string Statistics = "statistics";
    public const string Responses = "responses";
    public const string Events = "events";
    public const string Settings = "settings";
    /// <summary>
    /// Legacy CRM permission. Existing role claims are expanded to the granular
    /// CRM permissions during normalization.
    /// </summary>
    public const string Crm = "crm";
    public const string CrmBoard = "crm-board";
    public const string CrmTasks = "crm-tasks";
    public const string CrmAnalytics = "crm-analytics";
    public const string CrmTeam = "crm-team";
    public const string Administration = "administration";
    public const string ConfigurationClaimType = "orbita.permission-configured";
    public const string UserPermissionOverrideClaimType = "orbita.user-permission-override";
    public const string PermissionUpgradeClaimType = "orbita.permission-upgrade";
    public const string CrmAnalyticsUpgrade = "crm-analytics-v1";
    public const string CrmTeamUpgrade = "crm-team-v1";
    /// <summary>Strips team/analytics from Manager role so desk-only defaults stick after upgrades.</summary>
    public const string ManagerDeskOnlyUpgrade = "manager-desk-v1";
    public const string ListingsUpgrade = "listings-v1";

    public static readonly IReadOnlyList<PanelPermissionDefinition> All =
    [
        new(Dashboard, "Панель управления", "Сводка по работе офисов."),
        new(Workers, "Воркеры", "Просмотр и управление воркерами."),
        new(Accounts, "Аккаунты", "Просмотр аккаунтов и их состояния."),
        new(Balances, "Балансы", "Контроль балансов и пополнение аккаунтов Avito."),
        new(Listings, "Объявления", "Контроль сроков размещения объявлений Avito."),
        new(Statistics, "Статистика", "Просмотр аналитики по откликам."),
        new(Responses, "Отклики", "Работа с откликами и их доставкой."),
        new(Events, "События", "Просмотр и обработка событий."),
        new(Settings, "Личные настройки", "Профиль, пароль и личные интеграции."),
        new(CrmBoard, "CRM: Воронка", "Рабочее место менеджера и карточки кандидатов."),
        new(CrmTasks, "CRM: Задачи", "Список, выполнение и планирование задач CRM."),
        new(CrmAnalytics, "CRM: Аналитика", "Воронка, результаты и нагрузка команды CRM."),
        new(CrmTeam, "Команда CRM", "Нагрузка команды, очередь и настройки CRM офиса."),
        new(Administration, "Администрирование", "Пользователи, офисы, системные настройки и профили доступа.")
    ];

    public static readonly IReadOnlyList<PanelAccessProfileDefinition> Profiles =
    [
        new("admin", PanelRoles.Admin, "Администратор", "Технический доступ к панели и настройкам (не CRM desk)."),
        new(
            "office-lead",
            PanelRoles.OfficeLead,
            "Руководитель",
            "Полный доступ CRM и панели в своём офисе, без раздела «Администрирование»."),
        new(
            "senior-manager",
            PanelRoles.SeniorManager,
            "Старший менеджер",
            "Лиды всех менеджеров офиса, раздел «Команда», перераспределение лидов."),
        new("manager", PanelRoles.Manager, "Менеджер", "Только свои лиды CRM и личные настройки."),
        new("operator", PanelRoles.Operator, "Оператор", "Работа с панелью мониторинга и личными настройками.")
    ];

    public static IReadOnlyList<string> DefaultForRole(string? role) =>
        PanelRoles.Normalize(role) switch
        {
            PanelRoles.Admin => All.Select(x => x.Id).ToArray(),
            PanelRoles.OfficeLead => All
                .Where(x => x.Id is not Administration and not Balances and not Listings)
                .Select(x => x.Id)
                .ToArray(),
            PanelRoles.SeniorManager =>
            [
                CrmBoard, CrmTasks, CrmAnalytics, CrmTeam, Settings
            ],
            // Manager: only own CRM desk work — no team board / analytics by default.
            PanelRoles.Manager => [CrmBoard, CrmTasks, Settings],
            PanelRoles.Operator => [Dashboard, Workers, Accounts, Balances, Listings, Statistics, Responses, Events, Settings],
            _ => [Dashboard, Workers, Accounts, Statistics, Responses, Events, Settings]
        };

    public static bool NeedsCrmAnalyticsUpgrade(IEnumerable<string>? permissions)
    {
        var current = permissions?.ToHashSet(StringComparer.Ordinal) ?? [];
        return !current.Contains(CrmAnalytics)
               && (current.Contains(CrmBoard) || current.Contains(Crm));
    }

    /// <summary>
    /// Pre-permission gate was Administration + CRM board + CRM tasks.
    /// Grant the explicit tab to users who already had that combination.
    /// </summary>
    public static bool NeedsListingsUpgrade(string? role, IEnumerable<string>? permissions)
    {
        var current = permissions?.ToHashSet(StringComparer.Ordinal) ?? [];
        if (current.Contains(Listings))
        {
            return false;
        }

        var normalized = PanelRoles.Normalize(role);
        return normalized is PanelRoles.Admin or PanelRoles.Operator;
    }

    public static bool NeedsCrmTeamUpgrade(IEnumerable<string>? permissions)
    {
        var current = permissions?.ToHashSet(StringComparer.Ordinal) ?? [];
        return !current.Contains(CrmTeam)
               && current.Contains(Administration)
               && current.Contains(CrmBoard)
               && current.Contains(CrmTasks);
    }

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? permissions)
    {
        var allowed = All.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        void Add(string permission)
        {
            if (normalized.Add(permission))
            {
                result.Add(permission);
            }
        }

        foreach (var permission in permissions ?? [])
        {
            if (permission == Crm)
            {
                Add(CrmBoard);
                Add(CrmTasks);
                Add(CrmAnalytics);
            }
            else if (allowed.Contains(permission))
            {
                Add(permission);
            }
        }

        return result;
    }
}

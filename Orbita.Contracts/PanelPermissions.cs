namespace Orbita.Contracts;

public sealed record PanelPermissionDefinition(string Id, string Label, string Description);
public sealed record PanelAccessProfileDefinition(string Id, string Role, string Name, string Description);

public static class PanelPermissions
{
    public const string ClaimType = "orbita.permission";
    public const string Dashboard = "dashboard";
    public const string Workers = "workers";
    public const string Accounts = "accounts";
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
    public const string Administration = "administration";
    public const string ConfigurationClaimType = "orbita.permission-configured";
    public const string UserPermissionOverrideClaimType = "orbita.user-permission-override";

    public static readonly IReadOnlyList<PanelPermissionDefinition> All =
    [
        new(Dashboard, "Панель управления", "Сводка по работе офисов."),
        new(Workers, "Воркеры", "Просмотр и управление воркерами."),
        new(Accounts, "Аккаунты", "Просмотр аккаунтов и их состояния."),
        new(Statistics, "Статистика", "Просмотр аналитики по откликам."),
        new(Responses, "Отклики", "Работа с откликами и их доставкой."),
        new(Events, "События", "Просмотр и обработка событий."),
        new(Settings, "Личные настройки", "Профиль, пароль и личные интеграции."),
        new(CrmBoard, "CRM: Воронка", "Рабочее место менеджера и карточки кандидатов."),
        new(CrmTasks, "CRM: Задачи", "Список, выполнение и планирование задач CRM."),
        new(Administration, "Администрирование", "Пользователи, офисы, системные настройки и профили доступа.")
    ];

    public static readonly IReadOnlyList<PanelAccessProfileDefinition> Profiles =
    [
        new("admin", PanelRoles.Admin, "Администратор", "Полный доступ к панели и настройкам."),
        new("manager", PanelRoles.Manager, "Менеджер", "Работа с CRM и личными настройками."),
        new("operator", PanelRoles.Operator, "Оператор", "Работа с панелью мониторинга и личными настройками.")
    ];

    public static IReadOnlyList<string> DefaultForRole(string? role) =>
        PanelRoles.Normalize(role) switch
        {
            PanelRoles.Admin => All.Select(x => x.Id).ToArray(),
            PanelRoles.Manager => [CrmBoard, CrmTasks, Settings],
            _ => [Dashboard, Workers, Accounts, Statistics, Responses, Events, Settings]
        };

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
            }
            else if (allowed.Contains(permission))
            {
                Add(permission);
            }
        }

        return result;
    }
}

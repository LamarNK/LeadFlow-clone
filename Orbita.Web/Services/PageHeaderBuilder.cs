using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

public static class PageHeaderBuilder
{
    public static PageHeaderViewModel Create(
        string title,
        string? subtitle = null,
        bool showRefresh = true,
        bool showDateRange = false,
        DashboardPeriod? period = null,
        DateTime? updatedAtUtc = null)
    {
        var header = new PageHeaderViewModel
        {
            Title = title,
            Subtitle = subtitle,
            ShowRefresh = showRefresh,
            ShowDateRange = showDateRange,
            UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow
        };

        if (period is not null)
        {
            return new PageHeaderViewModel
            {
                Title = header.Title,
                Subtitle = header.Subtitle,
                ShowRefresh = header.ShowRefresh,
                ShowDateRange = header.ShowDateRange,
                UpdatedAtUtc = header.UpdatedAtUtc,
                DateRangeLabel = period.Label,
                DateFrom = period.From,
                DateTo = period.To,
                ActivePeriodPreset = period.ActivePreset
            };
        }

        return header;
    }

    public static PageHeaderViewModel WithOfficeScope(PageHeaderViewModel header, IOfficeContext office)
    {
        if (!office.IsAdmin || office.ShowAllOffices || string.IsNullOrWhiteSpace(office.ContextLabel))
        {
            return header;
        }

        var subtitle = string.IsNullOrWhiteSpace(header.Subtitle)
            ? office.ContextLabel
            : $"{header.Subtitle} · {office.ContextLabel}";

        return new PageHeaderViewModel
        {
            Title = header.Title,
            Subtitle = subtitle,
            ShowRefresh = header.ShowRefresh,
            ShowDateRange = header.ShowDateRange,
            UpdatedAtUtc = header.UpdatedAtUtc,
            DateRangeLabel = header.DateRangeLabel,
            DateFrom = header.DateFrom,
            DateTo = header.DateTo,
            ActivePeriodPreset = header.ActivePeriodPreset,
            UserDisplayName = header.UserDisplayName,
            UserEmail = header.UserEmail,
            UserInitial = header.UserInitial
        };
    }

    public static PageHeaderViewModel WorkersList() =>
        Create("Все воркеры", "Мониторинг и управление VDS-воркерами");

    public static PageHeaderViewModel WorkerDetails(string displayName, string? machineName, DateTime updatedAtUtc)
    {
        var subtitle = Formatting.WorkerDisplay.ShouldShowMachineName(displayName, machineName)
            ? $"{Formatting.WorkerDisplay.FormatMachineSubtitle(machineName!)} · Детали воркера и аккаунты"
            : "Детали воркера и аккаунты";
        return Create(displayName, subtitle, updatedAtUtc: updatedAtUtc);
    }

    public static PageHeaderViewModel AccountsList() =>
        Create("Аккаунты", "Статус, баланс и активность аккаунтов Avito");

    public static PageHeaderViewModel ResponsesList(DashboardPeriod period) =>
        Create("Отклики", "Входящие отклики за выбранный период", showDateRange: true, period: period);

    public static PageHeaderViewModel EventsList() =>
        Create("События", "Журнал событий воркеров и аккаунтов");

    public static PageHeaderViewModel ErrorsList() =>
        Create("Ошибки", "Агрегированные ошибки и предупреждения");

    public static PageHeaderViewModel MySettings() =>
        Create("Настройки", "Профиль и интеграция с Bitrix24", showRefresh: false);

    public static PageHeaderViewModel SettingsAdmin() =>
        Create("Администрирование", "Пользователи, воркеры, интеграции Bitrix, аудит и логи", showRefresh: false);

    public static PageHeaderViewModel Statistics(DashboardPeriod period) =>
        Create(
            "Статистика",
            "Балансы, динамика откликов и HR-метрики по офису",
            showDateRange: true,
            period: period);
}
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

    public static PageHeaderViewModel WorkersList() =>
        Create("Все воркеры", "Мониторинг и управление VDS-воркерами");

    public static PageHeaderViewModel WorkerDetails(string displayName, DateTime updatedAtUtc) =>
        Create(displayName, "Детали воркера и аккаунты", updatedAtUtc: updatedAtUtc);

    public static PageHeaderViewModel AccountsList() =>
        Create("Аккаунты", "Статус, баланс и активность аккаунтов Avito");

    public static PageHeaderViewModel ResponsesList(DashboardPeriod period) =>
        Create("Отклики", "Входящие отклики за выбранный период", showDateRange: true, period: period);

    public static PageHeaderViewModel EventsList() =>
        Create("События", "Журнал событий воркеров и аккаунтов");

    public static PageHeaderViewModel ErrorsList() =>
        Create("Ошибки", "Агрегированные ошибки и предупреждения");
}
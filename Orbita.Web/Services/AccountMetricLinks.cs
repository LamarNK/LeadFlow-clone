using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class AccountMetricLinks
{
    public static MetricLinkViewModel Responses(Guid workerId, Guid accountId, int count) =>
        Create(count, KpiCardLinks.AccountTodayResponses(workerId, accountId), "Отклики за сегодня");

    public static MetricLinkViewModel Unique(Guid workerId, Guid accountId, int count) =>
        Create(count, KpiCardLinks.AccountTodayUnique(workerId, accountId), "Уникальные отклики за сегодня");

    public static MetricLinkViewModel Errors(Guid workerId, Guid accountId, int count) =>
        Create(count, KpiCardLinks.AccountErrors(workerId, accountId), "Проблемы за сегодня");

    public static AccountMetricHrefViewModel Hrefs(Guid workerId, Guid accountId) => new(
        KpiCardLinks.AccountTodayResponses(workerId, accountId),
        KpiCardLinks.AccountTodayUnique(workerId, accountId),
        KpiCardLinks.AccountErrors(workerId, accountId));

    private static MetricLinkViewModel Create(int count, string? href, string title) => new()
    {
        Value = count,
        Href = count > 0 ? href : null,
        Title = title
    };
}
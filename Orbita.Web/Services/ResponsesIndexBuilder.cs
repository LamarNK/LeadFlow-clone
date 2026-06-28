using Orbita.Contracts;
using Orbita.Web.Formatting;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class ResponsesIndexBuilder
{
    public const int DefaultPageSize = 10;

    public static readonly EventFilterOptionViewModel[] StatusOptions =
    [
        new() { Value = "", Label = "Все статусы" },
        new() { Value = "unique", Label = "Уникальный" },
        new() { Value = "duplicate", Label = "Дубль" },
        new() { Value = "sent", Label = "Отправлен в Bitrix24" },
        new() { Value = "error", Label = "Ошибка обработки" }
    ];

    public static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(ResponsesSummaryDto summary)
    {
        var total = Math.Max(1, summary.Total);
        string Pct(int value) => $"{value * 100.0 / total:0.#}%";

        return
        [
            new()
            {
                Label = "Всего откликов",
                Value = summary.Total.ToString(),
                CountValue = summary.Total,
                Delta = "За выбранный период",
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-inbox",
                IconTone = "blue"
            },
            new()
            {
                Label = "Уникальных",
                Value = summary.Unique.ToString(),
                CountValue = summary.Unique,
                Delta = Pct(summary.Unique),
                DeltaTone = "good",
                IconClass = "fa-regular fa-circle-check",
                IconTone = "green"
            },
            new()
            {
                Label = "Дублей",
                Value = summary.Duplicates.ToString(),
                CountValue = summary.Duplicates,
                Delta = Pct(summary.Duplicates),
                DeltaTone = "neutral",
                IconClass = "fa-solid fa-clone",
                IconTone = "orange"
            },
            new()
            {
                Label = "Уникальных авторов",
                Value = summary.UniqueAuthors.ToString(),
                CountValue = summary.UniqueAuthors,
                Delta = "За выбранный период",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-user",
                IconTone = "blue"
            },
            new()
            {
                Label = "Среднее время отклика",
                Value = ResponseDisplay.FormatAverageResponseMinutes(summary.AvgResponseMinutes),
                CountValue = summary.AvgResponseMinutes ?? 0,
                ValueSuffix = summary.AvgResponseMinutes is > 0 ? null : "",
                Delta = "Среднее время между публикацией объявления и откликом",
                DeltaTone = "neutral",
                IconClass = "fa-regular fa-clock",
                IconTone = "blue"
            }
        ];
    }

    public static ResponseRowViewModel MapRow(ResponseListItemDto item) =>
        new()
        {
            Id = item.Id,
            CreatedAtUtc = item.CreatedAt,
            FullName = item.FullName,
            PhoneRaw = item.PhoneRaw,
            PhoneNormalized = item.PhoneNormalized,
            Vacancy = item.Vacancy,
            VacancyUrl = item.VacancyUrl,
            MessengerUrl = item.MessengerUrl,
            SourceResponseId = item.SourceResponseId,
            City = item.City,
            AccountId = item.AccountId,
            AccountName = item.AccountName,
            WorkerId = item.WorkerId,
            WorkerName = FormatWorkerName(item.WorkerName),
            Source = string.IsNullOrWhiteSpace(item.Source) ? "Avito" : item.Source,
            Status = item.Status,
            StatusLabel = MapStatusLabel(item.Status),
            StatusTone = MapStatusTone(item.Status),
            IsPhoneHidden = ResponseDisplay.IsPhoneHidden(item.PhoneRaw, item.PhoneNormalized),
            HasMessenger = !string.IsNullOrWhiteSpace(item.MessengerUrl),
            BitrixEntityId = item.BitrixEntityId,
            CanResend = item.Status is ResponseStatuses.Error
                or ResponseStatuses.ActionRequired
                or ResponseStatuses.InProgress
        };

    public static ResponseDetailViewModel MapDetail(ResponseDetailDto detail)
    {
        var (label, tone) = (MapStatusLabel(detail.Status), MapStatusTone(detail.Status));
        return new ResponseDetailViewModel
        {
            Id = detail.Id,
            FullName = detail.FullName,
            FirstName = detail.FirstName,
            LastName = detail.LastName,
            MiddleName = detail.MiddleName,
            Age = detail.Age,
            PhoneRaw = detail.PhoneRaw,
            PhoneNormalized = detail.PhoneNormalized,
            City = detail.City,
            Vacancy = detail.Vacancy,
            VacancyUrl = detail.VacancyUrl,
            MessengerUrl = detail.MessengerUrl,
            AccountId = detail.AccountId,
            AccountName = detail.AccountName,
            WorkerId = detail.WorkerId,
            WorkerName = FormatWorkerName(detail.WorkerName),
            Source = string.IsNullOrWhiteSpace(detail.Source) ? "Avito" : detail.Source,
            SourceResponseId = detail.SourceResponseId,
            Status = detail.Status,
            StatusLabel = label,
            StatusTone = tone,
            DuplicateSummary = detail.DuplicateSummary,
            BitrixEntityId = detail.BitrixEntityId,
            ErrorMessage = detail.ErrorMessage,
            RawText = detail.RawText,
            CreatedAtUtc = detail.CreatedAt,
            ProcessedAtUtc = detail.ProcessedAt,
            CanResend = detail.Status is ResponseStatuses.Error
                or ResponseStatuses.ActionRequired
                or ResponseStatuses.InProgress
        };
    }

    public static IReadOnlyList<EventFilterOptionViewModel> BuildWorkerOptions(IReadOnlyList<WorkerListItem> workers)
    {
        var options = new List<EventFilterOptionViewModel>
        {
            new() { Value = "", Label = "Все воркеры" }
        };
        options.AddRange(workers
            .OrderBy(w => w.DisplayName)
            .Select(w => new EventFilterOptionViewModel
            {
                Value = w.Id.ToString(),
                Label = FormatWorkerName(w.DisplayName)
            }));
        return options;
    }

    public static IReadOnlyList<EventFilterOptionViewModel> BuildAccountOptions(
        IReadOnlyList<ResponseFilterAccountDto> accounts)
    {
        var options = new List<EventFilterOptionViewModel>
        {
            new() { Value = "", Label = "Все аккаунты" }
        };
        options.AddRange(accounts
            .OrderBy(a => a.AccountName)
            .Select(a => new EventFilterOptionViewModel
            {
                Value = a.AccountId.ToString(),
                Label = a.AccountName
            }));
        return options;
    }

    private static string MapStatusLabel(string status) => status switch
    {
        ResponseStatuses.Duplicate => "Дубль",
        ResponseStatuses.Sent => "Отправлен",
        ResponseStatuses.Error or ResponseStatuses.ActionRequired => "Ошибка",
        _ => "Уникальный"
    };

    private static string MapStatusTone(string status) => status switch
    {
        ResponseStatuses.Duplicate => "duplicate",
        ResponseStatuses.Sent => "sent",
        ResponseStatuses.Error or ResponseStatuses.ActionRequired => "error",
        _ => "unique"
    };

    private static string FormatWorkerName(string workerDisplayName)
    {
        if (workerDisplayName.StartsWith("VDS-", StringComparison.OrdinalIgnoreCase))
        {
            var hash = Math.Abs(workerDisplayName.GetHashCode());
            return $"Worker #{(hash % 12) + 1}";
        }

        return workerDisplayName;
    }
}
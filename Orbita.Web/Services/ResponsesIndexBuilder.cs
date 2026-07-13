using Orbita.Contracts;
using Orbita.Web.Formatting;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class ResponsesIndexBuilder
{
    public const int DefaultPageSize = ListPageSizeDefaults.Responses;

    public static readonly EventFilterOptionViewModel[] StatusOptions =
    [
        new() { Value = "", Label = "Все статусы" },
        new() { Value = "unique", Label = "Уникальный" },
        new() { Value = "duplicate", Label = "Дубль" },
        new() { Value = "sent", Label = "Отправлен в Bitrix24" },
        new() { Value = "action_required", Label = "Ожидает CRM" },
        new() { Value = "error", Label = "Ошибка Bitrix" }
    ];

    public static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(
        ResponsesSummaryDto summary,
        DateTime from,
        DateTime to,
        Guid? workerId = null,
        Guid? accountId = null)
    {
        var total = Math.Max(1, summary.Total);
        string Pct(int value) => $"{value * 100.0 / total:0.#}%";

        return
        [
            new()
            {
                Key = "total",
                Href = KpiCardLinks.ResponsesCard("total", from, to, workerId, accountId),
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
                Key = "unique",
                Href = KpiCardLinks.ResponsesCard("unique", from, to, workerId, accountId),
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
                Key = "duplicates",
                Href = KpiCardLinks.ResponsesCard("duplicates", from, to, workerId, accountId),
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
                Key = "sent",
                Href = KpiCardLinks.ResponsesCard("sent", from, to, workerId, accountId),
                Label = "В Битрикс24",
                Value = summary.Sent.ToString(),
                CountValue = summary.Sent,
                Delta = Pct(summary.Sent),
                DeltaTone = summary.Sent > 0 ? "good" : "neutral",
                IconClass = "fa-solid fa-paper-plane",
                IconTone = "green"
            },
            new()
            {
                Key = "unique_authors",
                Href = KpiCardLinks.ResponsesCard("unique_authors", from, to, workerId, accountId),
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

    public static ResponseRowViewModel MapRow(ResponseListItemDto item)
    {
        var canSend = CanSendToBitrix(item.Status);
        var statusLabel = MapStatusLabel(item);
        var bitrixDeliveries = MapDeliveries(item.BitrixDeliveries);
        return new()
        {
            Id = item.Id,
            CreatedAtUtc = item.CreatedAt,
            FullName = item.FullName,
            Age = item.Age,
            PhoneRaw = item.PhoneRaw,
            PhoneNormalized = item.PhoneNormalized,
            Vacancy = item.Vacancy,
            VacancyUrl = item.VacancyUrl,
            MessengerUrl = item.MessengerUrl,
            SourceResponseId = item.SourceResponseId,
            City = item.City,
            AccountId = item.AccountId,
            AccountName = item.AccountName,
            AvitoSubProfileName = item.AvitoSubProfileName,
            WorkerId = item.WorkerId,
            WorkerName = FormatWorkerName(item.WorkerName),
            Source = string.IsNullOrWhiteSpace(item.Source) ? "Avito" : item.Source,
            Status = item.Status,
            StatusLabel = statusLabel,
            StatusTone = MapStatusTone(item.Status),
            IsPhoneHidden = ResponseDisplay.IsPhoneHidden(item.PhoneRaw, item.PhoneNormalized),
            HasMessenger = !string.IsNullOrWhiteSpace(item.MessengerUrl),
            BitrixEntityId = item.BitrixEntityId,
            BitrixEntityUrl = item.BitrixEntityUrl,
            BitrixLabel = MapBitrixColumn(item),
            BitrixDeliveries = bitrixDeliveries,
            CardCopy = BuildCardCopy(
                item.FullName,
                item.PhoneRaw,
                item.PhoneNormalized,
                item.City,
                item.Age,
                item.Vacancy,
                item.AccountName,
                item.AvitoSubProfileName,
                statusLabel,
                item.CreatedAt,
                item.ProcessedAt,
                item.VacancyUrl,
                item.MessengerUrl,
                item.BitrixDeliveries,
                item.BitrixEntityType,
                item.BitrixEntityId),
            CanSend = canSend,
            CanResend = canSend
        };
    }

    public static ResponseDetailViewModel MapDetail(ResponseDetailDto detail)
    {
        var listItem = new ResponseListItemDto(
            detail.Id,
            detail.OfficeId,
            detail.WorkerId,
            detail.WorkerName,
            detail.AccountId,
            detail.AccountName,
            detail.Source,
            detail.SourceResponseId,
            detail.FullName,
            detail.Age,
            detail.PhoneRaw,
            detail.PhoneNormalized,
            detail.Vacancy,
            detail.VacancyUrl,
            detail.MessengerUrl,
            detail.City,
            detail.Status,
            detail.IsLocalDuplicate,
            detail.IsBitrixDuplicate,
            detail.BitrixEntityId,
            detail.BitrixEntityType,
            detail.BitrixEntityUrl,
            detail.BitrixInstanceId,
            detail.BitrixInstanceName,
            detail.BitrixInstanceSignature,
            detail.DuplicateBitrixInstanceId,
            detail.DuplicateBitrixInstanceName,
            detail.AvitoSubProfileId,
            detail.AvitoSubProfileName,
            detail.CreatedAt,
            detail.ProcessedAt,
            detail.BitrixDeliveries);
        var canSend = CanSendToBitrix(detail.Status);
        var statusLabel = MapStatusLabel(listItem);
        return new ResponseDetailViewModel
        {
            Id = detail.Id,
            FullName = detail.FullName,
            FirstName = detail.FirstName,
            LastName = detail.LastName,
            MiddleName = detail.MiddleName,
            Age = detail.Age,
            Gender = detail.Gender,
            PhoneRaw = detail.PhoneRaw,
            PhoneNormalized = detail.PhoneNormalized,
            City = detail.City,
            Vacancy = detail.Vacancy,
            VacancyUrl = detail.VacancyUrl,
            MessengerUrl = detail.MessengerUrl,
            AccountId = detail.AccountId,
            AccountName = detail.AccountName,
            AvitoSubProfileId = string.IsNullOrWhiteSpace(detail.AvitoSubProfileId) ? null : detail.AvitoSubProfileId,
            AvitoSubProfileName = detail.AvitoSubProfileName,
            WorkerId = detail.WorkerId,
            WorkerName = FormatWorkerName(detail.WorkerName),
            Source = string.IsNullOrWhiteSpace(detail.Source) ? "Avito" : detail.Source,
            SourceResponseId = detail.SourceResponseId,
            Status = detail.Status,
            StatusLabel = statusLabel,
            StatusTone = MapStatusTone(detail.Status),
            DuplicateSummary = detail.DuplicateSummary,
            BitrixEntityId = detail.BitrixEntityId,
            BitrixEntityUrl = detail.BitrixEntityUrl,
            BitrixInstanceName = detail.BitrixInstanceName,
            BitrixInstanceSignature = detail.BitrixInstanceSignature,
            DuplicateBitrixInstanceName = detail.DuplicateBitrixInstanceName,
            ErrorMessage = detail.ErrorMessage,
            RawText = detail.RawText,
            ChatMessages = ResponseChatDisplay.ParseMessages(detail.ChatMessagesJson),
            CreatedAtUtc = detail.CreatedAt,
            ProcessedAtUtc = detail.ProcessedAt,
            BitrixDeliveries = MapDeliveries(detail.BitrixDeliveries),
            CardCopy = BuildCardCopy(
                detail.FullName,
                detail.PhoneRaw,
                detail.PhoneNormalized,
                detail.City,
                detail.Age,
                detail.Vacancy,
                detail.AccountName,
                detail.AvitoSubProfileName,
                statusLabel,
                detail.CreatedAt,
                detail.ProcessedAt,
                detail.VacancyUrl,
                detail.MessengerUrl,
                detail.BitrixDeliveries,
                detail.BitrixEntityType,
                detail.BitrixEntityId,
                detail.ErrorMessage),
            CanSend = canSend,
            CanResend = canSend
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

    public static IReadOnlyList<EventFilterOptionViewModel> BuildBitrixDestinationOptions(
        IReadOnlyList<BitrixInstanceListItemDto> instances)
    {
        var options = new List<EventFilterOptionViewModel>
        {
            new() { Value = "", Label = "Все Битриксы" },
            new() { Value = "not_sent", Label = "Не отправлен" }
        };
        options.AddRange(instances
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .Select(x => new EventFilterOptionViewModel
            {
                Value = x.Id.ToString(),
                Label = FormatBitrixLabel(x.Name, x.Signature)
            }));
        return options;
    }

    public static readonly EventFilterOptionViewModel[] GenderOptions =
    [
        new() { Value = "", Label = "Любой пол" },
        new() { Value = CandidateGenders.Male, Label = "Мужчина" },
        new() { Value = CandidateGenders.Female, Label = "Женщина" },
        new() { Value = CandidateGenders.Unknown, Label = "Не указан" }
    ];

    public static IReadOnlyList<EventFilterOptionViewModel> BuildVacancyOptions(
        IReadOnlyList<ResponseFilterVacancyDto> vacancies) =>
        vacancies
            .Where(x => !string.IsNullOrWhiteSpace(x.Vacancy))
            .Select(x => new EventFilterOptionViewModel
            {
                Value = x.Vacancy,
                Label = x.Count > 0 ? $"{x.Vacancy} ({x.Count})" : x.Vacancy
            })
            .ToList();

    public static string MapStatusMessageLabel(string status) => status switch
    {
        ResponseStatuses.ActionRequired => "Примечание",
        ResponseStatuses.Error => "Ошибка Bitrix",
        _ => "Сообщение"
    };

    public static IReadOnlyList<SendBitrixInstanceOptionViewModel> MapSendBitrixInstances(
        IReadOnlyList<BitrixInstanceListItemDto> instances) =>
        instances
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .Select(x => new SendBitrixInstanceOptionViewModel
            {
                Id = x.Id,
                Label = FormatBitrixLabel(x.Name, x.Signature),
                PortalHost = x.PortalHost
            })
            .ToList();

    private static bool CanSendToBitrix(string _) => true;

    private static string? MapBitrixColumn(ResponseListItemDto item)
    {
        if (item.BitrixDeliveries.Count > 0)
        {
            return null;
        }

        return item.Status switch
        {
            ResponseStatuses.Sent => FormatBitrixLabel(item.BitrixInstanceName, item.BitrixInstanceSignature),
            ResponseStatuses.Duplicate when item.IsBitrixDuplicate => item.DuplicateBitrixInstanceName,
            ResponseStatuses.Error => FormatBitrixLabel(item.BitrixInstanceName, item.BitrixInstanceSignature),
            _ => null
        };
    }

    private static IReadOnlyList<ResponseBitrixDeliveryViewModel> MapDeliveries(
        IReadOnlyList<ResponseBitrixDeliveryDto> deliveries) =>
        deliveries
            .Select(d => new ResponseBitrixDeliveryViewModel
            {
                Id = d.Id,
                BitrixLabel = d.BitrixLabel,
                Outcome = d.Outcome,
                OutcomeLabel = MapDeliveryOutcomeLabel(d.Outcome),
                ChipTone = MapDeliveryChipTone(d.Outcome),
                BitrixEntityUrl = d.BitrixEntityUrl,
                ErrorMessage = d.ErrorMessage,
                CreatedAtUtc = d.CreatedAtUtc
            })
            .ToList();

    private static string MapDeliveryOutcomeLabel(string outcome) => outcome switch
    {
        ResponseBitrixDeliveryOutcomes.Sent => "отправлен",
        ResponseBitrixDeliveryOutcomes.Duplicate => "дубль",
        ResponseBitrixDeliveryOutcomes.Error => "ошибка",
        ResponseBitrixDeliveryOutcomes.Unavailable => "недоступен",
        _ => outcome
    };

    private static string MapDeliveryChipTone(string outcome) => outcome switch
    {
        ResponseBitrixDeliveryOutcomes.Sent => "sent",
        ResponseBitrixDeliveryOutcomes.Duplicate => "duplicate",
        ResponseBitrixDeliveryOutcomes.Error => "error",
        ResponseBitrixDeliveryOutcomes.Unavailable => "unavailable",
        _ => "muted"
    };

    private static string MapStatusLabel(ResponseListItemDto item) => item.Status switch
    {
        ResponseStatuses.Duplicate => FormatWithBitrix("Дубль", item.DuplicateBitrixInstanceName),
        ResponseStatuses.Sent => FormatWithBitrix("Отправлен", FormatBitrixLabel(item.BitrixInstanceName, item.BitrixInstanceSignature)),
        ResponseStatuses.ActionRequired => "Ожидает CRM",
        ResponseStatuses.Error => FormatWithBitrix("Ошибка Bitrix", FormatBitrixLabel(item.BitrixInstanceName, item.BitrixInstanceSignature)),
        _ => "Уникальный"
    };

    private static string FormatBitrixLabel(string? name, string? signature) =>
        !string.IsNullOrWhiteSpace(signature) ? signature
        : !string.IsNullOrWhiteSpace(name) ? name
        : string.Empty;

    private static string FormatWithBitrix(string baseLabel, string? bitrixLabel) =>
        string.IsNullOrWhiteSpace(bitrixLabel) ? baseLabel : $"{baseLabel} · {bitrixLabel}";

    private static string FormatDeliveriesSummary(IReadOnlyList<ResponseBitrixDeliveryViewModel> deliveries) =>
        string.Join("\n", deliveries.Select(d =>
        {
            var line = $"{d.BitrixLabel} — {d.OutcomeLabel}";
            if (!string.IsNullOrWhiteSpace(d.ErrorMessage))
            {
                line += $" ({d.ErrorMessage})";
            }

            return line;
        }));

    private static string BuildCardCopy(
        string fullName,
        string phoneRaw,
        string phoneNormalized,
        string? city,
        int? age,
        string vacancy,
        string accountName,
        string? avitoSubProfileName,
        string statusLabel,
        DateTime createdAtUtc,
        DateTime? processedAtUtc,
        string? vacancyUrl,
        string? messengerUrl,
        IReadOnlyList<ResponseBitrixDeliveryDto> bitrixDeliveries,
        string? bitrixEntityType,
        string? bitrixEntityId,
        string? errorMessage = null)
    {
        var phoneHidden = ResponseDisplay.IsPhoneHidden(phoneRaw, phoneNormalized);
        var phoneDisplay = phoneHidden
            ? "Скрыт"
            : ResponseDisplay.FormatPhone(phoneRaw, phoneNormalized);
        return ResponseCardText.Format(
            ResponseDisplay.DisplayAuthor(fullName),
            phoneDisplay,
            city,
            age,
            vacancy,
            ResponseDisplay.FormatAccountWithSubProfile(accountName, avitoSubProfileName),
            statusLabel,
            createdAtUtc,
            processedAtUtc,
            vacancyUrl,
            messengerUrl,
            bitrixDeliveries,
            bitrixEntityType,
            bitrixEntityId,
            errorMessage);
    }

    private static string MapStatusTone(string status) => status switch
    {
        ResponseStatuses.Duplicate => "duplicate",
        ResponseStatuses.Sent => "sent",
        ResponseStatuses.ActionRequired => "action-required",
        ResponseStatuses.Error => "error",
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

    public static bool HasActiveFilters(ResponsesFilterViewModel filters, DashboardPeriod period) =>
        !string.IsNullOrWhiteSpace(filters.Status)
        || filters.WorkerId.HasValue
        || filters.AccountId.HasValue
        || !string.IsNullOrWhiteSpace(filters.BitrixDestination)
        || !string.IsNullOrWhiteSpace(filters.Gender)
        || filters.AgeFrom.HasValue
        || filters.AgeTo.HasValue
        || !string.IsNullOrWhiteSpace(filters.VacancyQuery)
        || !string.IsNullOrWhiteSpace(filters.SearchQuery)
        || (!period.IsTodayOnly && !period.IsAllTime);

    public static ResponseDetailJsonViewModel MapDetailJson(ResponseDetailViewModel detail)
    {
        var phone = ResponseDisplay.FormatPhone(detail.PhoneRaw, detail.PhoneNormalized);
        var sections = new List<DetailSectionItemViewModel>
        {
            new() { Label = "Телефон", Value = phone.Length > 0 ? phone : "Скрыт" },
            new() { Label = "Возраст", Value = detail.Age?.ToString() ?? "—" },
            new() { Label = "Пол", Value = CandidateGenders.FormatLabel(detail.Gender) },
            new() { Label = "Город", Value = detail.City },
            new() { Label = "Объявление", Value = detail.Vacancy, Href = detail.VacancyUrl },
            new() { Label = "Аккаунт", Value = ResponseDisplay.FormatAccountWithSubProfile(detail.AccountName, detail.AvitoSubProfileName) },
            new() { Label = "Воркер", Value = detail.WorkerName },
            new() { Label = "Источник", Value = detail.Source },
            new() { Label = "ID отклика", Value = detail.SourceResponseId }
        };

        if (detail.BitrixDeliveries.Count > 0)
        {
            sections.Add(new DetailSectionItemViewModel
            {
                Label = "Отправки в Bitrix",
                Value = FormatDeliveriesSummary(detail.BitrixDeliveries)
            });
        }
        else
        {
            var bitrixLabel = FormatBitrixLabel(detail.BitrixInstanceName, detail.BitrixInstanceSignature);
            if (!string.IsNullOrWhiteSpace(bitrixLabel))
            {
                sections.Add(new DetailSectionItemViewModel { Label = "Битрикс", Value = bitrixLabel });
            }

            if (!string.IsNullOrWhiteSpace(detail.DuplicateBitrixInstanceName))
            {
                sections.Add(new DetailSectionItemViewModel
                {
                    Label = "Дубль в Битриксе",
                    Value = detail.DuplicateBitrixInstanceName
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(detail.BitrixEntityId) && detail.BitrixDeliveries.Count == 0)
        {
            sections.Add(new DetailSectionItemViewModel
            {
                Label = "Bitrix ID",
                Value = detail.BitrixEntityId,
                Href = detail.BitrixEntityUrl
            });
        }

        if (!string.IsNullOrWhiteSpace(detail.DuplicateSummary) && detail.BitrixDeliveries.Count == 0)
        {
            sections.Add(new DetailSectionItemViewModel { Label = "Дубль", Value = detail.DuplicateSummary });
        }

        if (!string.IsNullOrWhiteSpace(detail.ErrorMessage))
        {
            sections.Add(new DetailSectionItemViewModel
            {
                Label = MapStatusMessageLabel(detail.Status),
                Value = detail.ErrorMessage
            });
        }

        var links = new List<DetailActionLinkViewModel>();
        if (!string.IsNullOrWhiteSpace(detail.VacancyUrl))
        {
            links.Add(new DetailActionLinkViewModel { Label = "Объявление", Href = detail.VacancyUrl, External = true });
        }
        foreach (var delivery in detail.BitrixDeliveries.Where(d =>
                     d.Outcome == ResponseBitrixDeliveryOutcomes.Sent &&
                     !string.IsNullOrWhiteSpace(d.BitrixEntityUrl)))
        {
            links.Add(new DetailActionLinkViewModel
            {
                Label = $"Bitrix24 · {delivery.BitrixLabel}",
                Href = delivery.BitrixEntityUrl!,
                External = true
            });
        }

        if (links.All(l => !l.Label.StartsWith("Bitrix24", StringComparison.Ordinal)) &&
            !string.IsNullOrWhiteSpace(detail.BitrixEntityUrl))
        {
            links.Add(new DetailActionLinkViewModel { Label = "Bitrix24", Href = detail.BitrixEntityUrl, External = true });
        }

        var primaryActions = new List<DetailActionLinkViewModel>();
        if (detail.CanSend)
        {
            primaryActions.Add(new DetailActionLinkViewModel
            {
                Label = "Отправить в Bitrix",
                Tone = "primary",
                Action = "send-bitrix",
                ResponseId = detail.Id
            });
        }

        return new ResponseDetailJsonViewModel
        {
            Title = ResponseDisplay.DisplayAuthor(detail.FullName),
            Subtitle = $"{detail.StatusLabel} · {ResponseDisplay.FormatCreatedAtLocal(detail.CreatedAtUtc)}",
            Sections = sections,
            ChatMessages = detail.ChatMessages
                .Select(m => new DetailChatMessageViewModel
                {
                    Text = m.Text,
                    Tone = m.Tone,
                    TimeLabel = m.TimeLabel
                })
                .ToList(),
            Links = links,
            PrimaryActions = primaryActions,
            CopyText = detail.CardCopy,
            CopyLabel = "Копировать карточку"
        };
    }
}
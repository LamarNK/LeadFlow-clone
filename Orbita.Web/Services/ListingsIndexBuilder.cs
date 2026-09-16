using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class ListingsIndexBuilder
{
    public const int DefaultPageSize = ListPageSizeDefaults.Listings;

    public static readonly AccountTabViewModel[] TabDefinitions =
    [
        new() { Id = "active", Label = "Активные" },
        new() { Id = "errors", Label = "С ошибками" },
        new() { Id = "unpublished", Label = "Неопубликованные" },
        new() { Id = "all", Label = "Все" }
    ];

    private static readonly HashSet<string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "worker", "account", "subprofile", "title", "id", "status", "published", "expires",
        "age", "remaining", "state", "source", "seen", "detail"
    };

    public static ListingsIndexViewModel Build(
        IReadOnlyList<AvitoAdListingListItem> items,
        AvitoAdListingSummary summary,
        string? searchQuery,
        string? tab,
        int page,
        int pageSize,
        string? sort,
        string? sortDir,
        IReadOnlyList<Guid>? workerIds,
        IReadOnlyList<Guid>? accountIds,
        IReadOnlyList<string>? subProfileIds,
        IReadOnlyList<EventFilterOptionViewModel> workers,
        IReadOnlyList<EventFilterOptionViewModel> accounts,
        IReadOnlyList<EventFilterOptionViewModel> subProfiles,
        IOfficeContext? officeContext = null,
        int? totalItems = null,
        bool itemsArePaged = false,
        IReadOnlyList<ListingAccountScopeViewModel>? accountScopes = null)
    {
        page = Math.Max(1, page);
        tab = NormalizeTab(tab);
        var selectedWorkers = workerIds ?? [];
        var selectedAccounts = accountIds ?? [];
        var selectedSubProfiles = subProfileIds ?? [];
        var tableSort = TableSort.Parse(sort, sortDir, TableSortState.Create("expires", descending: false), SortColumns);
        IReadOnlyList<ListingRowViewModel> paged;
        int total;
        if (itemsArePaged)
        {
            paged = items.Select(MapRow).ToList();
            total = totalItems ?? paged.Count;
        }
        else
        {
            var filtered = items
                .Where(x => selectedWorkers.Count == 0 || selectedWorkers.Contains(x.WorkerId))
                .Where(x => selectedAccounts.Count == 0 || selectedAccounts.Contains(x.AccountId))
                .Where(x => selectedSubProfiles.Count == 0
                            || selectedSubProfiles.Contains(x.AvitoSubProfileId, StringComparer.Ordinal))
                .Where(x => MatchesTab(x, tab))
                .Select(MapRow)
                .ToList();
            var sorted = ApplySort(filtered, tableSort).ToList();
            total = sorted.Count;
            paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        }

        var header = PageHeaderBuilder.Create("Объявления", "Контроль срока размещения объявлений Avito");
        if (officeContext is not null)
        {
            header = PageHeaderBuilder.WithOfficeScope(header, officeContext);
        }

        return new ListingsIndexViewModel
        {
            Header = header,
            SearchQuery = searchQuery,
            ActiveTab = tab,
            Tabs = TabDefinitions.Select(x => new AccountTabViewModel
            {
                Id = x.Id,
                Label = x.Label,
                Count = x.Id switch
                {
                    "active" => summary.ActiveCount,
                    "errors" => summary.ErrorCount,
                    "unpublished" => summary.UnpublishedCount,
                    _ => summary.ActiveCount + summary.ErrorCount + summary.UnpublishedCount
                }
            }).ToList(),
            Summary = summary,
            AccountScopes = accountScopes ?? [],
            OverallScopeMetrics = new ListingStatusMetricsViewModel
            {
                ActiveCount = (accountScopes ?? []).Sum(x => x.Metrics.ActiveCount),
                UnpublishedCount = (accountScopes ?? []).Sum(x => x.Metrics.UnpublishedCount),
                ErrorCount = (accountScopes ?? []).Sum(x => x.Metrics.ErrorCount)
            },
            KpiCards = BuildKpiCards(summary),
            Rows = paged,
            Pagination = new PaginationViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalItems = total
            },
            Sort = tableSort,
            WorkerIds = selectedWorkers,
            AccountIds = selectedAccounts,
            SubProfileIds = selectedSubProfiles,
            Workers = workers,
            Accounts = accounts,
            SubProfiles = subProfiles,
            HasActiveFilters = !string.IsNullOrWhiteSpace(searchQuery)
                || tab != "active"
                || selectedWorkers.Count > 0
                || selectedAccounts.Count > 0
                || selectedSubProfiles.Count > 0,
            ActiveFilterChips = FilterChipsBuilder.ForListings(
                searchQuery,
                tab,
                TabDefinitions,
                selectedWorkers,
                workers,
                selectedAccounts,
                accounts,
                selectedSubProfiles,
                subProfiles,
                pageSize)
        };
    }

    public static ListingRowViewModel MapRow(AvitoAdListingListItem item) =>
        new()
        {
            Id = item.Id,
            WorkerId = item.WorkerId,
            WorkerName = item.WorkerName,
            WorkerUrl = $"/Workers/Details/{item.WorkerId:D}",
            AccountId = item.AccountId,
            AccountName = item.AccountName,
            AccountUrl = "/Accounts?q=" + Uri.EscapeDataString(item.AccountName ?? string.Empty),
            AvitoSubProfileId = item.AvitoSubProfileId,
            SubProfileName = string.IsNullOrWhiteSpace(item.SubProfileName) ? "—" : item.SubProfileName,
            Title = item.Title,
            Url = item.Url ?? string.Empty,
            AvitoItemId = item.AvitoItemId,
            StatusText = string.IsNullOrWhiteSpace(item.StatusText) ? "Активно" : item.StatusText,
            PublishedAtUtc = item.PublishedAtUtc,
            ExpiresAtUtc = item.ExpiresAtUtc,
            AgeDays = item.AgeDays,
            RemainingDays = item.RemainingDays,
            State = item.State,
            StateLabel = item.SourceTab switch
            {
                "rejected" => "С ошибками",
                "inactive" => "Не опубликовано",
                _ => StateLabel(item.State)
            },
            StateTone = item.SourceTab switch
            {
                "rejected" => "danger",
                "inactive" => "muted",
                _ => StateTone(item.State)
            },
            PublicationDateSource = item.PublicationDateSource,
            PublicationDateSourceLabel = SourceLabel(item.PublicationDateSource),
            LastSeenAtUtc = item.LastSeenAtUtc,
            DetailCheckedAtUtc = item.DetailCheckedAtUtc,
            IsActive = item.IsActive,
            SourceTab = item.SourceTab,
            ErrorReason = !string.IsNullOrWhiteSpace(item.ErrorReason)
                ? item.ErrorReason
                : ParseErrorLabel(item.LastParseError),
            LastParseError = item.LastParseError,
            CanPublish = item.CanPublish,
            ImageUrl = item.ImageUrl,
            Salary = item.Salary,
            City = item.City,
            AddressText = item.AddressText,
            DistrictText = item.DistrictText,
            Views = item.Views,
            Contacts = item.Contacts,
            Favorites = item.Favorites
        };

    public static IReadOnlyList<DashboardKpiCardViewModel> BuildKpiCards(AvitoAdListingSummary summary) =>
    [
        new()
        {
            Key = "active",
            Href = "/Listings?tab=active",
            Label = "Активных",
            Value = summary.ActiveCount.ToString(),
            CountValue = summary.ActiveCount,
            Delta = "В реестре",
            DeltaTone = "neutral",
            IconClass = "fa-solid fa-bullhorn",
            IconTone = "blue"
        },
        new()
        {
            Key = "unknown",
            Href = "/Listings?tab=unknown",
            Label = "Без точной даты",
            Value = summary.UnknownDateCount.ToString(),
            CountValue = summary.UnknownDateCount,
            Delta = "Нужна детальная проверка",
            DeltaTone = summary.UnknownDateCount > 0 ? "warning" : "neutral",
            IconClass = "fa-regular fa-calendar-xmark",
            IconTone = "orange"
        },
        new()
        {
            Key = "expiring",
            Href = "/Listings?tab=expiring",
            Label = "Истекает за 7 дней",
            Value = summary.ExpiringIn7DaysCount.ToString(),
            CountValue = summary.ExpiringIn7DaysCount,
            Delta = "Контрольный срок",
            DeltaTone = summary.ExpiringIn7DaysCount > 0 ? "warning" : "neutral",
            IconClass = "fa-solid fa-hourglass-half",
            IconTone = "orange"
        },
        new()
        {
            Key = "today",
            Href = "/Listings?tab=today",
            Label = "Истекает сегодня",
            Value = summary.ExpiresTodayCount.ToString(),
            CountValue = summary.ExpiresTodayCount,
            Delta = "Срочно",
            DeltaTone = summary.ExpiresTodayCount > 0 ? "danger" : "neutral",
            IconClass = "fa-solid fa-bell",
            IconTone = "red"
        },
        new()
        {
            Key = "expired",
            Href = "/Listings?tab=all",
            Label = "Срок прошёл",
            Value = summary.ExpiredCount.ToString(),
            CountValue = summary.ExpiredCount,
            Delta = "Активные с истёкшим сроком",
            DeltaTone = summary.ExpiredCount > 0 ? "danger" : "neutral",
            IconClass = "fa-solid fa-triangle-exclamation",
            IconTone = "red"
        }
    ];

    public static string NormalizeTab(string? tab) =>
        tab switch
        {
            "all" or "errors" or "unpublished" or "notactive" or "expiring" or "today" or "unknown" or "parsefailed" or "active" => tab,
            _ => "active"
        };

    private static bool MatchesTab(AvitoAdListingListItem item, string tab) =>
        tab switch
        {
            "all" => true,
            "errors" => item.SourceTab == "rejected",
            "unpublished" => item.SourceTab == "inactive",
            "notactive" => !item.IsActive,
            "expiring" => item.IsActive && item.State == AvitoAdListingStates.ApproachingExpiry,
            "today" => item.IsActive && item.State == AvitoAdListingStates.ExpiresToday,
            "unknown" => item.IsActive && item.State == AvitoAdListingStates.UnknownPublicationDate,
            "parsefailed" => item.State == AvitoAdListingStates.ParseFailed,
            _ => item.IsActive
        };

    private static IEnumerable<ListingRowViewModel> ApplySort(IEnumerable<ListingRowViewModel> rows, TableSortState sort)
    {
        Func<ListingRowViewModel, object?> key = sort.Column switch
        {
            "worker" => x => x.WorkerName,
            "account" => x => x.AccountName,
            "subprofile" => x => x.SubProfileName,
            "title" => x => x.Title,
            "id" => x => x.AvitoItemId,
            "status" => x => x.StatusText,
            "published" => x => x.PublishedAtUtc ?? DateTime.MaxValue,
            "age" => x => x.AgeDays ?? int.MaxValue,
            "remaining" => x => x.RemainingDays ?? int.MaxValue,
            "state" => x => x.StateLabel,
            "source" => x => x.PublicationDateSourceLabel,
            "seen" => x => x.LastSeenAtUtc ?? DateTime.MinValue,
            "detail" => x => x.DetailCheckedAtUtc ?? DateTime.MinValue,
            _ => x => x.ExpiresAtUtc ?? DateTime.MaxValue
        };

        return sort.Descending
            ? rows.OrderByDescending(key).ThenBy(x => x.Title)
            : rows.OrderBy(key).ThenBy(x => x.Title);
    }

    public static string StateLabel(string state) =>
        state switch
        {
            AvitoAdListingStates.Active => "Активно",
            AvitoAdListingStates.ApproachingExpiry => "Срок близко",
            AvitoAdListingStates.ExpiresToday => "Истекает сегодня",
            AvitoAdListingStates.Expired => "Срок прошёл",
            AvitoAdListingStates.NotActive => "Неактивно",
            AvitoAdListingStates.UnknownPublicationDate => "Нет даты",
            AvitoAdListingStates.ParseFailed => "Ошибка разбора",
            _ => state
        };

    public static string StateTone(string state) =>
        state switch
        {
            AvitoAdListingStates.ApproachingExpiry => "warning",
            AvitoAdListingStates.ExpiresToday => "danger",
            AvitoAdListingStates.Expired => "danger",
            AvitoAdListingStates.ParseFailed => "danger",
            AvitoAdListingStates.UnknownPublicationDate => "warning",
            AvitoAdListingStates.NotActive => "muted",
            _ => "ok"
        };

    public static string SourceLabel(string source) =>
        source switch
        {
            AvitoAdPublicationDateSources.Exact => "Точная",
            AvitoAdPublicationDateSources.Estimated => "Оценка",
            AvitoAdPublicationDateSources.ListExpiry => "Срок из списка",
            _ => "Неизвестно"
        };

    private static string ParseErrorLabel(string? error) => error switch
    {
        "list_expiry_unparsed" => "Не удалось определить срок размещения по карточке Avito.",
        "list_expiry_text_empty" => "Avito не показал срок размещения в карточке.",
        "missing_view_link" => "В карточке отсутствует ссылка на объявление.",
        "empty_view_link_href" => "Ссылка объявления в карточке пустая.",
        "invalid_view_link_href" => "Avito вернул некорректную ссылку объявления.",
        null or "" => string.Empty,
        _ => error.Replace('_', ' ')
    };
}

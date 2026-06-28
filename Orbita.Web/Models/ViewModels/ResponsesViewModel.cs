namespace Orbita.Web.Models.ViewModels;

public sealed class ResponsesIndexViewModel
{
    public IReadOnlyList<BreadcrumbItemViewModel> Breadcrumbs { get; init; } =
    [
        new() { Label = "Главная", Url = "/Dashboard" },
        new() { Label = "Отклики", IsActive = true }
    ];

    public IReadOnlyList<DashboardKpiCardViewModel> KpiCards { get; init; } = [];
    public ResponsesFilterViewModel Filters { get; init; } = new();
    public string PeriodLabel { get; init; } = string.Empty;
    public string? ActivePeriodPreset { get; init; }
    public IReadOnlyList<EventFilterOptionViewModel> Statuses { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> Workers { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> Accounts { get; init; } = [];
    public IReadOnlyList<ResponseRowViewModel> Responses { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
    public ResponseDetailViewModel? Selected { get; init; }
}

public sealed record ResponsesFilterViewModel
{
    public string? Status { get; init; }
    public Guid? WorkerId { get; init; }
    public Guid? AccountId { get; init; }
    public string? VacancyQuery { get; init; }
    public string? SearchQuery { get; init; }
    public DateTime DateFrom { get; init; } = DateTime.Today;
    public DateTime DateTo { get; init; } = DateTime.Today;
    public int Page { get; init; } = 1;
}

public sealed class ResponseRowViewModel
{
    public Guid Id { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string PhoneRaw { get; init; } = string.Empty;
    public string PhoneNormalized { get; init; } = string.Empty;
    public string Vacancy { get; init; } = string.Empty;
    public string VacancyUrl { get; init; } = string.Empty;
    public string MessengerUrl { get; init; } = string.Empty;
    public string SourceResponseId { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public Guid AccountId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = string.Empty;
    public string Source { get; init; } = "Avito";
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusTone { get; init; } = "unique";
    public bool IsPhoneHidden { get; init; }
    public bool HasMessenger { get; init; }
    public string? BitrixEntityId { get; init; }
    public bool CanResend { get; init; }
}

public sealed class ResponseDetailViewModel
{
    public Guid Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string MiddleName { get; init; } = string.Empty;
    public int? Age { get; init; }
    public string PhoneRaw { get; init; } = string.Empty;
    public string PhoneNormalized { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string Vacancy { get; init; } = string.Empty;
    public string VacancyUrl { get; init; } = string.Empty;
    public string MessengerUrl { get; init; } = string.Empty;
    public Guid AccountId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = string.Empty;
    public string Source { get; init; } = "Avito";
    public string SourceResponseId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusTone { get; init; } = "unique";
    public string? DuplicateSummary { get; init; }
    public string? BitrixEntityId { get; init; }
    public string? ErrorMessage { get; init; }
    public string RawText { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? ProcessedAtUtc { get; init; }
    public bool CanResend { get; init; }
}
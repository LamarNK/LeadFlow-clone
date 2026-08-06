using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class ResponsesIndexViewModel
{
    public PageHeaderViewModel Header { get; init; } = new();
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
    public IReadOnlyList<EventFilterOptionViewModel> BitrixDestinations { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> Genders { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> Vacancies { get; init; } = [];
    public IReadOnlyList<ResponseRowViewModel> Responses { get; init; } = [];
    public IReadOnlyList<SendBitrixInstanceOptionViewModel> SendBitrixInstances { get; init; } = [];
    public IReadOnlyList<EventFilterOptionViewModel> OfficeOptions { get; init; } = [];
    /// <summary>Offices for the deliver modal (includes CRM acceptance flag).</summary>
    public IReadOnlyList<DeliveryOfficeOptionViewModel> DeliveryOffices { get; init; } = [];
    public PaginationViewModel Pagination { get; init; } = new();
    public ResponseDetailViewModel? Selected { get; init; }
    public bool HasActiveFilters { get; init; }
    public IReadOnlyList<ActiveFilterChipViewModel> ActiveFilterChips { get; init; } = [];
    public int ActiveFilterCount => ActiveFilterChips.Count;
    public TableSortState Sort { get; init; } = TableSortState.Create("time", descending: true);
}

public sealed record ResponsesFilterViewModel
{
    public string? Status { get; init; }
    public Guid? WorkerId { get; init; }
    public Guid? AccountId { get; init; }
    public string? BitrixDestination { get; init; }
    public string? Gender { get; init; }
    public int? AgeFrom { get; init; }
    public int? AgeTo { get; init; }
    public string? VacancyQuery { get; init; }
    public string? SearchQuery { get; init; }
    public DateTime DateFrom { get; init; } = DateTime.Today;
    public DateTime DateTo { get; init; } = DateTime.Today;
    public int Page { get; init; } = 1;
}

public sealed class ResponseRowViewModel
{
    public Guid Id { get; init; }
    public Guid PersonId { get; init; }
    public DateTime CollectedAtUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string FullName { get; init; } = string.Empty;
    public int? Age { get; init; }
    public string? Gender { get; init; }
    public string PhoneRaw { get; init; } = string.Empty;
    public string PhoneNormalized { get; init; } = string.Empty;
    public string Vacancy { get; init; } = string.Empty;
    public string VacancyUrl { get; init; } = string.Empty;
    public string MessengerUrl { get; init; } = string.Empty;
    public string? AvatarUrl { get; init; }
    public string SourceResponseId { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public Guid AccountId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public string? AvitoSubProfileName { get; init; }
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = string.Empty;
    public string Source { get; init; } = "Avito";
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusTone { get; init; } = "unique";
    public bool IsPhoneHidden { get; init; }
    public bool HasMessenger { get; init; }
    public string? BitrixEntityId { get; init; }
    public string? BitrixEntityUrl { get; init; }
    public string? BitrixLabel { get; init; }
    public IReadOnlyList<ResponseBitrixDeliveryViewModel> BitrixDeliveries { get; init; } = [];
    public IReadOnlyList<ResponseCrmDeliveryViewModel> CrmDeliveries { get; init; } = [];
    public string CardCopy { get; init; } = string.Empty;
    public bool IsHighlighted { get; init; }
    public string? HighlightLabel { get; init; }
    public IReadOnlyList<string> HighlightLabels { get; init; } = [];
    public string? PhoneMetricKind { get; init; }
    public string? PhoneMetricLabel { get; init; }
    public string? PreviousPhoneRaw { get; init; }
    public string? PreviousPhoneNormalized { get; init; }
    public bool CanSend { get; init; }
    public bool CanResend { get; init; }
}

public sealed class ResponseBitrixDeliveryViewModel
{
    public Guid Id { get; init; }
    public string BitrixLabel { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string OutcomeLabel { get; init; } = string.Empty;
    public string ChipTone { get; init; } = "muted";
    public string? BitrixEntityUrl { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class ResponseCrmDeliveryViewModel
{
    public Guid Id { get; init; }
    public string OfficeName { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string OutcomeLabel { get; init; } = string.Empty;
    public string ChipTone { get; init; } = "muted";
    public string? ErrorMessage { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class SendBitrixInstanceOptionViewModel
{
    public required Guid Id { get; init; }
    public required string Label { get; init; }
    public string? PortalHost { get; init; }
}

public sealed class DeliveryOfficeOptionViewModel
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool CrmEnabled { get; init; }
}

public sealed class ResponsesDeliverOptionsViewModel
{
    public IReadOnlyList<DeliveryOfficeOptionViewModel> DeliveryOffices { get; init; } = [];
    public IReadOnlyList<SendBitrixInstanceOptionViewModel> SendBitrixInstances { get; init; } = [];
}

public sealed class ResponseDetailViewModel
{
    public Guid Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string MiddleName { get; init; } = string.Empty;
    public int? Age { get; init; }
    public string? Gender { get; init; }
    public string PhoneRaw { get; init; } = string.Empty;
    public string PhoneNormalized { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string Vacancy { get; init; } = string.Empty;
    public string VacancyUrl { get; init; } = string.Empty;
    public string MessengerUrl { get; init; } = string.Empty;
    public string? AvatarUrl { get; init; }
    public Guid AccountId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public string? AvitoSubProfileId { get; init; }
    public string? AvitoSubProfileName { get; init; }
    public Guid WorkerId { get; init; }
    public string WorkerName { get; init; } = string.Empty;
    public string Source { get; init; } = "Avito";
    public string SourceResponseId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusTone { get; init; } = "unique";
    public string? PhoneMetricKind { get; init; }
    public string? PhoneMetricLabel { get; init; }
    public string? PreviousPhoneRaw { get; init; }
    public string? PreviousPhoneNormalized { get; init; }
    public string? DuplicateSummary { get; init; }
    public string? BitrixEntityId { get; init; }
    public string? BitrixEntityUrl { get; init; }
    public string? BitrixInstanceName { get; init; }
    public string? BitrixInstanceSignature { get; init; }
    public string? DuplicateBitrixInstanceName { get; init; }
    public string? ErrorMessage { get; init; }
    public string RawText { get; init; } = string.Empty;
    public IReadOnlyList<Formatting.ResponseChatMessageViewModel> ChatMessages { get; init; } = [];
    public DateTime CollectedAtUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? ProcessedAtUtc { get; init; }
    public IReadOnlyList<ResponseBitrixDeliveryViewModel> BitrixDeliveries { get; init; } = [];
    public IReadOnlyList<CandidatePhoneHistoryDto> PhoneHistory { get; init; } = [];
    public string CardCopy { get; init; } = string.Empty;
    public bool CanSend { get; init; }
    public bool CanResend { get; init; }
}

public sealed class BulkSendResponsesToBitrixFormModel
{
    public List<Guid> ResponseIds { get; set; } = [];
    public Guid BitrixInstanceId { get; set; }
}

public sealed class SendResponseToBitrixFormModel
{
    public Guid Id { get; set; }
    public Guid BitrixInstanceId { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Status { get; set; }
    public Guid? WorkerId { get; set; }
    public Guid? AccountId { get; set; }
    public string? BitrixDestination { get; set; }
    public string? Gender { get; set; }
    public int? AgeFrom { get; set; }
    public int? AgeTo { get; set; }
    public string? Vacancy { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public string? Sort { get; set; }
    public string? Dir { get; set; }
}

public sealed class DeliverResponseFormModel
{
    public Guid Id { get; set; }
    public Guid? OfficeId { get; set; }
    public List<Guid> OfficeIds { get; set; } = [];
    public bool ToCrm { get; set; } = true;
    public bool ToBitrix { get; set; }
    public Guid? BitrixInstanceId { get; set; }
    public List<Guid> BitrixInstanceIds { get; set; } = [];
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Status { get; set; }
    public Guid? WorkerId { get; set; }
    public Guid? AccountId { get; set; }
    public string? BitrixDestination { get; set; }
    public string? Gender { get; set; }
    public int? AgeFrom { get; set; }
    public int? AgeTo { get; set; }
    public string? Vacancy { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public string? Sort { get; set; }
    public string? Dir { get; set; }
}

public sealed class BulkDeliverResponsesFormModel
{
    public List<Guid> ResponseIds { get; set; } = [];
    public Guid? OfficeId { get; set; }
    public List<Guid> OfficeIds { get; set; } = [];
    public bool ToCrm { get; set; } = true;
    public bool ToBitrix { get; set; }
    public Guid? BitrixInstanceId { get; set; }
    public List<Guid> BitrixInstanceIds { get; set; } = [];
}

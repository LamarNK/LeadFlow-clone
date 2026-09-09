namespace Orbita.Contracts;

/// <summary>One page, three reports with explicit and different counting bases.</summary>
public sealed record CrmSalesAnalyticsDto(
    IReadOnlyList<CrmSalesMetricDto> Results,
    IReadOnlyList<CrmSalesBreakdownDto> ContactSources,
    IReadOnlyList<CrmSalesBreakdownDto> CloseReasons,
    IReadOnlyList<CrmSalesOfficeDto> Offices,
    IReadOnlyList<CrmSalesManagerDto> Managers,
    CrmSalesCohortDto Cohort,
    int UnclassifiedClosures,
    int InferredEvents,
    int UnattributedActions = 0);

public sealed record CrmSalesMetricDto(string Key, string Label, int Count, string Unit, string Description);
public sealed record CrmSalesBreakdownDto(string Key, string Label, int Count);
public sealed record CrmSalesTransitionDto(string Key, string From, string To, int Count);
public sealed record CrmSalesStageDto(string Key, string Stage, int Count, bool Archived);
public sealed record CrmSalesOfficeDto(Guid OfficeId, string OfficeName,
    IReadOnlyList<CrmSalesStageDto> Stages, IReadOnlyList<CrmSalesTransitionDto> Transitions);
public sealed record CrmSalesManagerDto(Guid OfficeId, string OfficeName, string UserId, string DisplayName,
    int NewLeads, int Contacts, int Questionnaires, int Tickets, int Successes, int Refusals,
    int Transitions, int FirstAssignments, int TransfersReceived, int TransfersSent);
public sealed record CrmSalesCohortMetricDto(string Key, string Label, int Count, double? Percent);
public sealed record CrmSalesCohortDto(int Received, int Unassigned,
    IReadOnlyList<CrmSalesCohortMetricDto> Results);

public static class CrmSalesMetrics
{
    public const string Received = "sales.received";
    public const string Contacts = "sales.contacts";
    public const string Questionnaires = "sales.questionnaires";
    public const string Tickets = "sales.tickets";
    public const string Successes = "sales.successes";
    public const string Refusals = "sales.refusals";
    public const string UnknownClosures = "sales.unknown-closures";
    public const string Unassigned = "sales.unassigned";
    public const string UnattributedActions = "sales.unattributed-actions";
}

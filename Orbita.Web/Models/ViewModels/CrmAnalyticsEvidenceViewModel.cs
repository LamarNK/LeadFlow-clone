using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class CrmAnalyticsEvidenceViewModel
{
    public PageHeaderViewModel Header { get; init; } = new();
    public CrmAnalyticsEvidenceDto? Data { get; init; }
    public string Metric { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string? ManagerUserId { get; init; }
    public string? ReturnManagerUserId { get; init; }
    public string CohortBasis { get; init; } = "received";
    public Guid? EvidenceOfficeId { get; init; }
    public string Report { get; init; } = "activity";
    public string LeadView { get; init; } = "snapshot";
    public int TimeZoneOffset { get; init; }
}

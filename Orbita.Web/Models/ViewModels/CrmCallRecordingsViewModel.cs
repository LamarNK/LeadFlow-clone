using Orbita.Contracts;

namespace Orbita.Web.Models.ViewModels;

public sealed class CrmCallRecordingsViewModel
{
    public PageHeaderViewModel Header { get; init; } = new();
    public CrmCallRecordingsDto? Data { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string? ManagerUserId { get; init; }
    public string? Phone { get; init; }
    public string? CandidateName { get; init; }
    public string? Direction { get; init; }
    public int TimeZoneOffset { get; init; }
}

namespace LeadFlow.Core.Services.AdsPower;

public sealed record AdsPowerProfileSummary(
    string UserId,
    string Name,
    string? SerialNumber,
    string? GroupName,
    string? GroupId = null);

public sealed record AdsPowerGroupSummary(string GroupId, string GroupName);

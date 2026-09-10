namespace Orbita.Contracts;

public sealed record CrmMissedCallDto(
    Guid Id, Guid? CardId, string? CandidateName, string Phone, string CalledPhone,
    DateTime StartedAtUtc, string Status, string? ResponsibleName, string OfficeName);

public sealed record CrmMissedCallManagerDto(string Id, string Name);

public sealed record CrmMissedCallsDto(
    int Total, int Missed, int Rejected, int Failed, int Page, int PageSize,
    IReadOnlyList<CrmMissedCallDto> Rows, IReadOnlyList<CrmMissedCallManagerDto> Managers);

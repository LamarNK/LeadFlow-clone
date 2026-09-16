namespace Orbita.Contracts;

public sealed record CrmCallRecordingRowDto(
    Guid Id, Guid? CardId, string? CandidateName, string Phone,
    DateTime StartedAtUtc, int DurationSeconds, string Direction,
    string? ResponsibleName, string OfficeName);

public sealed record CrmCallRecordingManagerDto(string Id, string Name);

public sealed record CrmCallRecordingsDto(
    int Total, int Page, int PageSize, IReadOnlyList<CrmCallRecordingRowDto> Rows,
    IReadOnlyList<CrmCallRecordingManagerDto> Managers);

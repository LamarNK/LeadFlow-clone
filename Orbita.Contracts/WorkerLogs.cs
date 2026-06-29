namespace Orbita.Contracts;

public sealed record WorkerLogEntryUploadDto(
    DateTime TimestampUtc,
    string Level,
    string Source,
    string Message,
    string? TraceId,
    bool IsTampered);

public sealed record WorkerLogsBatchRequest(IReadOnlyList<WorkerLogEntryUploadDto> Entries);

public sealed record WorkerLogEntryDto(
    DateTime TimestampUtc,
    string Level,
    string Source,
    string Message,
    string? TraceId,
    bool IsTampered);

public sealed record WorkerLogsPageDto(
    IReadOnlyList<WorkerLogEntryDto> Items,
    int Total,
    int Page,
    int PageSize);
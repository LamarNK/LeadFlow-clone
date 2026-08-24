namespace Orbita.Contracts;

public sealed record WorkerLogEntryUploadDto(
    DateTime TimestampUtc,
    string Level,
    string Source,
    string Message,
    string? TraceId,
    bool IsTampered,
    Dictionary<string, string>? Properties = null);

public sealed record WorkerLogsBatchRequest(IReadOnlyList<WorkerLogEntryUploadDto> Entries);

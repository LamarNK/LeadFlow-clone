namespace Orbita.Contracts;

public sealed record WorkerDiagnosticUploadResponse(Guid AttachmentId);

public sealed record WorkerDiagnosticAttachmentDto(
    Guid Id,
    Guid WorkerId,
    Guid? AccountId,
    string Kind,
    string? PageUrl,
    DateTime CreatedAtUtc,
    long SizeBytes);
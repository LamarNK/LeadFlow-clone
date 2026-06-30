namespace Orbita.Contracts;

public sealed record LeadFlowImportPreviewDto(
    Guid SessionId,
    Guid OfficeId,
    string OfficeName,
    int TotalInFile,
    int ImportableCount,
    int AlreadyExistsCount,
    int InvalidCount,
    bool IsEncrypted,
    int PreviewSampleCount,
    IReadOnlyList<LeadFlowImportPreviewItemDto> Items);

public sealed record LeadFlowImportPreviewItemDto(
    Guid Id,
    string FullName,
    string PhoneRaw,
    string AccountName,
    string Vacancy,
    string Status,
    DateTime CreatedAt,
    bool CanImport,
    string ImportState,
    string? ExistingStatus);

public sealed record LeadFlowImportExecuteRequest(
    Guid SessionId,
    IReadOnlyList<Guid>? SelectedIds);

public sealed record LeadFlowImportExecuteResultDto(
    int Requested,
    int Imported,
    int Skipped,
    int Failed);
namespace Orbita.Contracts;

public sealed record WorkerReleaseInfoDto(
    string Version,
    string? ReleaseNotes,
    long FileSize,
    string Sha256,
    bool IsLatest,
    DateTime UploadedAtUtc);

public sealed record WorkerReleaseListResponse(
    WorkerReleaseInfoDto? Latest,
    IReadOnlyList<WorkerReleaseInfoDto> Versions);

public sealed record WorkerReleaseLatestDto(
    string Version,
    string? ReleaseNotes,
    long FileSize,
    string Sha256,
    string DownloadFileName,
    string PublicDownloadPath,
    DateTime UploadedAtUtc);

public sealed record WorkerUpdateCheckResponse(
    bool HasUpdate,
    string? LatestVersion,
    string? DownloadPath,
    string? Sha256,
    long FileSize,
    string? ReleaseNotes);

public sealed record WorkerUpdateResultDto(
    string Version,
    bool Success,
    string? Message,
    DateTime CompletedAtUtc);

public sealed record SetWorkerReleaseLatestRequest(string Version);

public sealed record DeleteWorkerReleaseRequest(string Version);
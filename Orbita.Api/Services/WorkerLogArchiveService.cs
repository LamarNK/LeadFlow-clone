using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;
using Orbita.Logging.Audit;

namespace Orbita.Api.Services;

/// <summary>
/// Archives worker audit records in the shared file log stream. Worker logs are
/// deliberately not persisted as application data in PostgreSQL.
/// </summary>
public sealed class WorkerLogArchiveService(OrbitaDbContext db, WorkerLogFileArchive archive)
{
    public const int MaxBatchSize = 2000;

    public async Task<(int Accepted, string? Error)> IngestBatchAsync(
        Guid workerId,
        IReadOnlyList<WorkerLogEntryUploadDto> entries,
        CancellationToken ct = default)
    {
        if (entries.Count == 0)
        {
            return (0, null);
        }

        if (entries.Count > MaxBatchSize)
        {
            return (0, $"Размер батча превышает {MaxBatchSize}.");
        }

        var worker = await db.Workers
            .AsNoTracking()
            .Where(x => x.Id == workerId)
            .Select(x => new { x.Id, x.DisplayName, x.MachineName })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (worker is null)
        {
            return (0, "Воркер не найден.");
        }

        await archive.AppendAsync(worker.Id, worker.DisplayName, worker.MachineName, entries, ct)
            .ConfigureAwait(false);
        return (entries.Count, null);
    }
}

/// <summary>Owns the single writer for the shared <c>Orbita.Worker</c> log directory.</summary>
public sealed class WorkerLogFileArchive
{
    private readonly Logger logger;

    public WorkerLogFileArchive(IConfiguration configuration)
    {
        var sharedRoot = configuration["Logs:SharedRoot"];
        var root = string.IsNullOrWhiteSpace(sharedRoot)
            ? GlobalLogger.ResolveLogsRootDirectory(string.Empty)
            : Path.GetFullPath(sharedRoot);
        logger = new Logger(Path.Combine(root, "Orbita.Worker"));
    }

    public async Task AppendAsync(
        Guid workerId,
        string? workerName,
        string? machineName,
        IReadOnlyList<WorkerLogEntryUploadDto> entries,
        CancellationToken ct)
    {
        var marker = $"worker:{workerId:D}";
        var displayName = string.IsNullOrWhiteSpace(workerName) ? machineName : workerName;
        var identity = string.IsNullOrWhiteSpace(displayName)
            ? marker
            : $"{marker} · {displayName.Trim()}";

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var originalSource = string.IsNullOrWhiteSpace(entry.Source) ? "—" : entry.Source.Trim();
            var sourceTamperMarker = entry.IsTampered ? " [source-tampered]" : string.Empty;
            await logger.AppendImportedAsync(
                    entry.TimestampUtc,
                    ParseLevel(entry.Level),
                    $"[{identity}] {originalSource}{sourceTamperMarker}",
                    entry.Message ?? string.Empty,
                    entry.TraceId,
                    new Dictionary<string, object?>
                    {
                        ["worker.id"] = workerId,
                        ["worker.name"] = displayName,
                        ["worker.machine"] = machineName,
                        ["worker.source"] = originalSource,
                        ["worker.source_is_tampered"] = entry.IsTampered
                    },
                    ct)
                .ConfigureAwait(false);
        }
    }

    private static DeskLinkAuditLogLevel ParseLevel(string? level) => level?.Trim() switch
    {
        "Error" => DeskLinkAuditLogLevel.Error,
        "Warning" => DeskLinkAuditLogLevel.Warning,
        "Debug" => DeskLinkAuditLogLevel.Debug,
        _ => DeskLinkAuditLogLevel.Info
    };
}

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerLogsService(
    OrbitaDbContext db,
    IOptions<WorkerLogsOptions> options)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    private readonly WorkerLogsOptions _options = options.Value;

    public async Task<(int Accepted, string? Error)> IngestBatchAsync(
        Guid workerId,
        IReadOnlyList<WorkerLogEntryUploadDto> entries,
        CancellationToken ct = default)
    {
        if (entries.Count == 0)
        {
            return (0, null);
        }

        if (entries.Count > _options.MaxBatchSize)
        {
            return (0, $"Размер батча превышает {_options.MaxBatchSize}.");
        }

        var workerExists = await db.Workers.AnyAsync(x => x.Id == workerId, ct).ConfigureAwait(false);
        if (!workerExists)
        {
            return (0, "Воркер не найден.");
        }

        var now = DateTime.UtcNow;
        var entities = entries
            .Select(entry => MapUpload(workerId, entry, now))
            .ToList();

        var hashes = entities.Select(x => x.DedupHash).Distinct().ToList();
        var existing = await db.WorkerLogEntries
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId && hashes.Contains(x.DedupHash))
            .Select(x => x.DedupHash)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var existingSet = existing.ToHashSet(StringComparer.Ordinal);
        var toInsert = entities
            .DistinctBy(x => x.DedupHash, StringComparer.Ordinal)
            .Where(x => !existingSet.Contains(x.DedupHash))
            .ToList();

        if (toInsert.Count == 0)
        {
            return (0, null);
        }

        db.WorkerLogEntries.AddRange(toInsert);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (toInsert.Count, null);
    }

    public async Task<WorkerLogsPageDto> SearchAsync(
        Guid workerId,
        string? searchText,
        string? level,
        DateTime? date,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.WorkerLogEntries
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId);

        if (!string.IsNullOrWhiteSpace(level))
        {
            var normalizedLevel = level.Trim();
            query = query.Where(x => x.Level == normalizedLevel);
        }

        if (date.HasValue)
        {
            var dayStart = DateOnly.FromDateTime(date.Value).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var dayEnd = dayStart.AddDays(1);
            query = query.Where(x => x.TimestampUtc >= dayStart && x.TimestampUtc < dayEnd);
        }

        foreach (var token in SearchQueryNormalizer.Tokenize(searchText))
        {
            var pattern = SearchQueryNormalizer.ToILikePattern(token);
            query = query.Where(x =>
                EF.Functions.ILike(x.Message, pattern)
                || EF.Functions.ILike(x.Source, pattern)
                || (x.TraceId != null && EF.Functions.ILike(x.TraceId, pattern)));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(x => x.TimestampUtc)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new WorkerLogEntryDto(
                x.TimestampUtc,
                x.Level,
                x.Source,
                x.Message,
                x.TraceId,
                x.IsTampered))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new WorkerLogsPageDto(items, total, page, pageSize);
    }

    public async Task<int> PruneExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays));
        var stale = await db.WorkerLogEntries
            .Where(x => x.TimestampUtc < cutoff)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (stale.Count == 0)
        {
            return 0;
        }

        db.WorkerLogEntries.RemoveRange(stale);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return stale.Count;
    }

    private static WorkerLogEntryEntity MapUpload(Guid workerId, WorkerLogEntryUploadDto entry, DateTime ingestedAtUtc)
    {
        var level = NormalizeLevel(entry.Level);
        var source = Truncate(entry.Source, 256);
        var message = entry.Message ?? string.Empty;
        var traceId = string.IsNullOrWhiteSpace(entry.TraceId) ? null : Truncate(entry.TraceId, 64);

        return new WorkerLogEntryEntity
        {
            WorkerId = workerId,
            TimestampUtc = EnsureUtc(entry.TimestampUtc),
            Level = level,
            Source = source,
            Message = message,
            TraceId = traceId,
            IsTampered = entry.IsTampered,
            IngestedAtUtc = ingestedAtUtc,
            DedupHash = BuildDedupHash(workerId, entry.TimestampUtc, level, source, message, traceId)
        };
    }

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string NormalizeLevel(string? level) => level?.Trim() switch
    {
        "Error" => "Error",
        "Warning" => "Warning",
        "Debug" => "Debug",
        _ => "Info"
    };

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "—";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string BuildDedupHash(
        Guid workerId,
        DateTime timestampUtc,
        string level,
        string source,
        string message,
        string? traceId)
    {
        var payload = $"{workerId:N}|{timestampUtc:O}|{level}|{source}|{message}|{traceId ?? ""}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }
}

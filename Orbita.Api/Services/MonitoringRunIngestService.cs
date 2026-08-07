using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>Приём и upsert журнала мониторинг-циклов от воркеров.</summary>
public sealed class MonitoringRunIngestService(OrbitaDbContext db)
{
    public const int MaxCyclesPerBatch = 50;
    public const int MaxSubProfilesPerCycle = 40;
    public const int RetentionDays = 60;

    private const int MaxSaveAttempts = 3;

    public async Task<(int Accepted, string? Error)> IngestBatchAsync(
        Guid workerId,
        MonitoringRunBatchRequest request,
        CancellationToken ct = default)
    {
        // Один и тот же цикл может прийти в двух перекрывающихся запросах (буфер
        // воркера шлёт батчи fire-and-forget): оба запроса могут прочитать цикл
        // как отсутствующий и попытаться вставить его одновременно. Проигравшая
        // вставка падает с unique violation, а из-за abort всего мульти-стейтмент
        // батча Npgsql маскирует её как DbUpdateConcurrencyException. Повторяем
        // сохранение со свежим чтением — сходится к update существующей строки.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await IngestCoreAsync(workerId, request, ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts - 1)
            {
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex) when (
                attempt < MaxSaveAttempts - 1
                && IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }

    private async Task<(int Accepted, string? Error)> IngestCoreAsync(
        Guid workerId,
        MonitoringRunBatchRequest request,
        CancellationToken ct = default)
    {
        if (request.WorkerId != workerId)
        {
            return (0, "WorkerId в теле не совпадает с токеном.");
        }

        var cycles = request.Cycles ?? [];
        if (cycles.Count == 0)
        {
            return (0, null);
        }

        if (cycles.Count > MaxCyclesPerBatch)
        {
            return (0, $"Не более {MaxCyclesPerBatch} циклов в одном батче.");
        }

        var workerExists = await db.Workers.AsNoTracking().AnyAsync(w => w.Id == workerId, ct);
        if (!workerExists)
        {
            return (0, "Воркер не найден.");
        }

        var nowUtc = DateTime.UtcNow;
        var accepted = 0;

        foreach (var cycleDto in cycles)
        {
            if (cycleDto.Id == Guid.Empty || cycleDto.AccountId == Guid.Empty)
            {
                continue;
            }

            var subDtos = cycleDto.SubProfiles ?? [];
            if (subDtos.Count > MaxSubProfilesPerCycle)
            {
                return (0, $"Не более {MaxSubProfilesPerCycle} субпрофилей на цикл.");
            }

            var status = NormalizeStatus(cycleDto.Status);
            var accountName = Truncate(cycleDto.AccountName?.Trim() ?? string.Empty, 200);
            if (string.IsNullOrWhiteSpace(accountName))
            {
                accountName = cycleDto.AccountId.ToString("D");
            }

            var existing = await db.MonitoringCycleRuns
                .Include(x => x.SubProfileRuns)
                .FirstOrDefaultAsync(x => x.Id == cycleDto.Id, ct);

            if (existing is null)
            {
                existing = new MonitoringCycleRunEntity
                {
                    Id = cycleDto.Id,
                    WorkerId = workerId,
                    AccountId = cycleDto.AccountId,
                    AccountName = accountName,
                    StartedAtUtc = EnsureUtc(cycleDto.StartedAtUtc),
                    FinishedAtUtc = cycleDto.FinishedAtUtc is null ? null : EnsureUtc(cycleDto.FinishedAtUtc.Value),
                    Status = status,
                    IngestedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc
                };
                db.MonitoringCycleRuns.Add(existing);
            }
            else
            {
                if (existing.WorkerId != workerId)
                {
                    continue;
                }

                existing.AccountId = cycleDto.AccountId;
                existing.AccountName = accountName;
                existing.StartedAtUtc = EnsureUtc(cycleDto.StartedAtUtc);
                existing.FinishedAtUtc = cycleDto.FinishedAtUtc is null
                    ? existing.FinishedAtUtc
                    : EnsureUtc(cycleDto.FinishedAtUtc.Value);
                existing.Status = status;
                existing.UpdatedAtUtc = nowUtc;
            }

            var byId = existing.SubProfileRuns.ToDictionary(x => x.Id);
            foreach (var subDto in subDtos)
            {
                if (subDto.Id == Guid.Empty || subDto.Position <= 0)
                {
                    continue;
                }

                var outcome = NormalizeOutcome(subDto.Outcome);
                var subName = Truncate(subDto.SubProfileName?.Trim() ?? string.Empty, 200);
                if (string.IsNullOrWhiteSpace(subName))
                {
                    subName = "—";
                }

                if (!byId.TryGetValue(subDto.Id, out var subEntity))
                {
                    subEntity = new MonitoringSubProfileRunEntity
                    {
                        Id = subDto.Id,
                        CycleRunId = existing.Id
                    };
                    existing.SubProfileRuns.Add(subEntity);
                    byId[subDto.Id] = subEntity;
                }

                subEntity.SubProfileId = Truncate(subDto.SubProfileId?.Trim() ?? string.Empty, 128);
                subEntity.SubProfileName = subName;
                subEntity.Position = subDto.Position;
                subEntity.Total = Math.Max(subDto.Total, subDto.Position);
                subEntity.StartedAtUtc = EnsureUtc(subDto.StartedAtUtc);
                subEntity.CompletedAtUtc = subDto.CompletedAtUtc is null
                    ? null
                    : EnsureUtc(subDto.CompletedAtUtc.Value);
                subEntity.Outcome = outcome;
                subEntity.ErrorType = TruncateOptional(subDto.ErrorType, 64);
                subEntity.ErrorMessage = TruncateOptional(subDto.ErrorMessage, 500);
                subEntity.FoundCount = Math.Max(0, subDto.FoundCount);
                subEntity.PublishedCount = Math.Max(0, subDto.PublishedCount);
                subEntity.DeferredCount = Math.Max(0, subDto.DeferredCount);
                subEntity.SkippedDuplicateCount = Math.Max(0, subDto.SkippedDuplicateCount);
            }

            accepted++;
        }

        if (accepted > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return (accepted, null);
    }

    public async Task<int> PruneExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        return await db.MonitoringCycleRuns
            .Where(x => x.StartedAtUtc < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    private static string NormalizeStatus(string? status) =>
        status?.Trim() switch
        {
            MonitoringCycleRunStatuses.Completed => MonitoringCycleRunStatuses.Completed,
            MonitoringCycleRunStatuses.Aborted => MonitoringCycleRunStatuses.Aborted,
            MonitoringCycleRunStatuses.Failed => MonitoringCycleRunStatuses.Failed,
            _ => MonitoringCycleRunStatuses.Running
        };

    private static string NormalizeOutcome(string? outcome) =>
        outcome?.Trim() switch
        {
            MonitoringSubProfileRunOutcomes.Completed => MonitoringSubProfileRunOutcomes.Completed,
            MonitoringSubProfileRunOutcomes.Failed => MonitoringSubProfileRunOutcomes.Failed,
            MonitoringSubProfileRunOutcomes.Skipped => MonitoringSubProfileRunOutcomes.Skipped,
            _ => MonitoringSubProfileRunOutcomes.Started
        };

    private static DateTime EnsureUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string? TruncateOptional(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

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

    private const int MaxEfSaveAttempts = 3;

    public async Task<(int Accepted, string? Error)> IngestBatchAsync(
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

        foreach (var cycleDto in cycles)
        {
            var subCount = cycleDto.SubProfiles?.Count ?? 0;
            if (subCount > MaxSubProfilesPerCycle)
            {
                return (0, $"Не более {MaxSubProfilesPerCycle} субпрофилей на цикл.");
            }
        }

        var workerExists = await db.Workers.AsNoTracking().AnyAsync(w => w.Id == workerId, ct);
        if (!workerExists)
        {
            return (0, "Воркер не найден.");
        }

        // PostgreSQL: настоящий upsert (ON CONFLICT) — безопасен при параллельных POST
        // одного и того же цикла. EF read-then-insert даёт unique violation / concurrency.
        if (db.Database.IsNpgsql())
        {
            return await IngestViaPostgresUpsertAsync(workerId, cycles, ct).ConfigureAwait(false);
        }

        // InMemory / SQLite в тестах — прежний EF-путь с retry.
        return await IngestViaEfAsync(workerId, cycles, ct).ConfigureAwait(false);
    }

    private async Task<(int Accepted, string? Error)> IngestViaPostgresUpsertAsync(
        Guid workerId,
        IReadOnlyList<MonitoringCycleRunUploadDto> cycles,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var accepted = 0;

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var cycleDto in cycles)
            {
                if (cycleDto.Id == Guid.Empty || cycleDto.AccountId == Guid.Empty)
                {
                    continue;
                }

                var status = NormalizeStatus(cycleDto.Status);
                var accountName = NormalizeAccountName(cycleDto.AccountName, cycleDto.AccountId);
                var startedAtUtc = EnsureUtc(cycleDto.StartedAtUtc);
                DateTime? finishedAtUtc = cycleDto.FinishedAtUtc is null
                    ? null
                    : EnsureUtc(cycleDto.FinishedAtUtc.Value);

                // rows = 0, если строка чужого воркера (DO UPDATE WHERE не сработал).
                var cycleRows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "MonitoringCycleRuns" (
                        "Id", "WorkerId", "AccountId", "AccountName",
                        "StartedAtUtc", "FinishedAtUtc", "Status",
                        "IngestedAtUtc", "UpdatedAtUtc")
                    VALUES (
                        {cycleDto.Id}, {workerId}, {cycleDto.AccountId}, {accountName},
                        {startedAtUtc}, {finishedAtUtc}, {status},
                        {nowUtc}, {nowUtc})
                    ON CONFLICT ("Id") DO UPDATE SET
                        "AccountId" = EXCLUDED."AccountId",
                        "AccountName" = EXCLUDED."AccountName",
                        "StartedAtUtc" = EXCLUDED."StartedAtUtc",
                        "FinishedAtUtc" = COALESCE(EXCLUDED."FinishedAtUtc", "MonitoringCycleRuns"."FinishedAtUtc"),
                        "Status" = EXCLUDED."Status",
                        "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc"
                    -- Журнал отправляется асинхронно: старый батч Running может
                    -- прийти после финального Aborted/Failed/Completed. Терминальный
                    -- цикл нельзя снова открыть запоздавшим снимком состояния.
                    WHERE "MonitoringCycleRuns"."WorkerId" = EXCLUDED."WorkerId"
                      AND "MonitoringCycleRuns"."Status" = {MonitoringCycleRunStatuses.Running}
                    """, ct).ConfigureAwait(false);

                if (cycleRows <= 0)
                {
                    continue;
                }

                foreach (var subDto in cycleDto.SubProfiles ?? [])
                {
                    if (subDto.Id == Guid.Empty || subDto.Position <= 0)
                    {
                        continue;
                    }

                    var outcome = NormalizeOutcome(subDto.Outcome);
                    var subName = NormalizeSubProfileName(subDto.SubProfileName);
                    var subProfileId = Truncate(subDto.SubProfileId?.Trim() ?? string.Empty, 128);
                    var position = subDto.Position;
                    var total = Math.Max(subDto.Total, subDto.Position);
                    var subStarted = EnsureUtc(subDto.StartedAtUtc);
                    DateTime? subCompleted = subDto.CompletedAtUtc is null
                        ? null
                        : EnsureUtc(subDto.CompletedAtUtc.Value);
                    var errorType = TruncateOptional(subDto.ErrorType, 64);
                    var errorMessage = TruncateOptional(subDto.ErrorMessage, 500);
                    var found = Math.Max(0, subDto.FoundCount);
                    var published = Math.Max(0, subDto.PublishedCount);
                    var deferred = Math.Max(0, subDto.DeferredCount);
                    var skippedDup = Math.Max(0, subDto.SkippedDuplicateCount);
                    var collected = Math.Max(0, subDto.CollectedCount);
                    var captcha = Math.Max(0, subDto.CaptchaCount);
                    var captchaSolved = Math.Min(captcha, Math.Max(0, subDto.CaptchaSolvedCount));

                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO "MonitoringSubProfileRuns" (
                            "Id", "CycleRunId", "SubProfileId", "SubProfileName",
                            "Position", "Total", "StartedAtUtc", "CompletedAtUtc",
                            "Outcome", "ErrorType", "ErrorMessage",
                            "FoundCount", "PublishedCount", "DeferredCount", "SkippedDuplicateCount",
                            "CollectedCount", "CaptchaCount", "CaptchaSolvedCount", "LoginAttempted", "LoginSucceeded")
                        VALUES (
                            {subDto.Id}, {cycleDto.Id}, {subProfileId}, {subName},
                            {position}, {total}, {subStarted}, {subCompleted},
                            {outcome}, {errorType}, {errorMessage},
                            {found}, {published}, {deferred}, {skippedDup},
                            {collected}, {captcha}, {captchaSolved}, {subDto.LoginAttempted}, {subDto.LoginSucceeded})
                        ON CONFLICT ("Id") DO UPDATE SET
                            "CycleRunId" = EXCLUDED."CycleRunId",
                            "SubProfileId" = EXCLUDED."SubProfileId",
                            "SubProfileName" = EXCLUDED."SubProfileName",
                            "Position" = EXCLUDED."Position",
                            "Total" = EXCLUDED."Total",
                            "StartedAtUtc" = EXCLUDED."StartedAtUtc",
                            "CompletedAtUtc" = EXCLUDED."CompletedAtUtc",
                            "Outcome" = EXCLUDED."Outcome",
                            "ErrorType" = EXCLUDED."ErrorType",
                            "ErrorMessage" = EXCLUDED."ErrorMessage",
                            "FoundCount" = EXCLUDED."FoundCount",
                            "PublishedCount" = EXCLUDED."PublishedCount",
                            "DeferredCount" = EXCLUDED."DeferredCount",
                            "SkippedDuplicateCount" = EXCLUDED."SkippedDuplicateCount",
                            "CollectedCount" = EXCLUDED."CollectedCount",
                            "CaptchaCount" = EXCLUDED."CaptchaCount",
                            "CaptchaSolvedCount" = EXCLUDED."CaptchaSolvedCount",
                            "LoginAttempted" = EXCLUDED."LoginAttempted",
                            "LoginSucceeded" = EXCLUDED."LoginSucceeded"
                    -- То же правило для под-профиля: Started не должен затереть
                    -- уже зафиксированный результат прохода.
                    WHERE "MonitoringSubProfileRuns"."Outcome" = {MonitoringSubProfileRunOutcomes.Started}
                    """, ct).ConfigureAwait(false);
                }

                accepted++;
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (accepted, null);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(int Accepted, string? Error)> IngestViaEfAsync(
        Guid workerId,
        IReadOnlyList<MonitoringCycleRunUploadDto> cycles,
        CancellationToken ct)
    {
        // Один и тот же цикл может прийти в двух перекрывающихся запросах.
        // На EF (InMemory/SQLite) нет ON CONFLICT — retry со свежим чтением.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await IngestViaEfCoreAsync(workerId, cycles, ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxEfSaveAttempts - 1)
            {
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex) when (
                attempt < MaxEfSaveAttempts - 1
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

    private async Task<(int Accepted, string? Error)> IngestViaEfCoreAsync(
        Guid workerId,
        IReadOnlyList<MonitoringCycleRunUploadDto> cycles,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var accepted = 0;

        foreach (var cycleDto in cycles)
        {
            if (cycleDto.Id == Guid.Empty || cycleDto.AccountId == Guid.Empty)
            {
                continue;
            }

            var status = NormalizeStatus(cycleDto.Status);
            var accountName = NormalizeAccountName(cycleDto.AccountName, cycleDto.AccountId);

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

                // Фоновый flush может доставить устаревший Running после terminal
                // snapshot. Состояние завершённого цикла намеренно неизменно.
                if (existing.Status != MonitoringCycleRunStatuses.Running)
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
            foreach (var subDto in cycleDto.SubProfiles ?? [])
            {
                if (subDto.Id == Guid.Empty || subDto.Position <= 0)
                {
                    continue;
                }

                var outcome = NormalizeOutcome(subDto.Outcome);
                var subName = NormalizeSubProfileName(subDto.SubProfileName);

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
                subEntity.CollectedCount = Math.Max(0, subDto.CollectedCount);
                subEntity.CaptchaCount = Math.Max(0, subDto.CaptchaCount);
                subEntity.CaptchaSolvedCount = Math.Min(
                    subEntity.CaptchaCount,
                    Math.Max(0, subDto.CaptchaSolvedCount));
                subEntity.LoginAttempted = subDto.LoginAttempted;
                subEntity.LoginSucceeded = subDto.LoginSucceeded;
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

    private static string NormalizeAccountName(string? accountName, Guid accountId)
    {
        var name = Truncate(accountName?.Trim() ?? string.Empty, 200);
        return string.IsNullOrWhiteSpace(name) ? accountId.ToString("D") : name;
    }

    private static string NormalizeSubProfileName(string? name)
    {
        var subName = Truncate(name?.Trim() ?? string.Empty, 200);
        return string.IsNullOrWhiteSpace(subName) ? "—" : subName;
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

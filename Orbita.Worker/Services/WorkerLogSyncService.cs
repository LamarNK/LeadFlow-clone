using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services.Worker;
using Microsoft.Extensions.Hosting;
using Orbita.Contracts;
using Orbita.Worker;

namespace Orbita.Worker.Services;

public sealed class WorkerLogSyncService(
    IWorkerLogsUploader uploader,
    WorkerLogSyncState syncState) : BackgroundService
{
    /// <summary>Пауза, когда локальный хвост пуст (нет новых записей).</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);

    /// <summary>Пауза после сетевой/API ошибки, чтобы не крутить CPU/API вхолостую.</summary>
    private static readonly TimeSpan ErrorRetryInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Размер батча. Должен быть ≤ <c>WorkerLogs:MaxBatchSize</c> на API.
    /// Полный батч = «ещё есть хвост» → сразу следующий запрос без IdleInterval.
    /// </summary>
    internal const int MaxBatchSize = 2000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        syncState.Load();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var outcome = await SyncOnceAsync(stoppingToken).ConfigureAwait(false);
                switch (outcome)
                {
                    case SyncOutcome.BacklogRemaining:
                        // Хвост есть — сразу следующий батч, без минутной паузы.
                        continue;
                    case SyncOutcome.Failed:
                        await Task.Delay(ErrorRetryInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    default:
                        await Task.Delay(IdleInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(ErrorRetryInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Один проход синка. <see cref="SyncOutcome.BacklogRemaining"/> — полный батч ушёл, сразу слать ещё.
    /// </summary>
    internal async Task<SyncOutcome> SyncOnceAsync(CancellationToken ct)
    {
        var sinceUtc = syncState.LastSyncedUtc;
        var entries = await GlobalLogger.Instance
            .ReadEntriesNewerThanAsync(sinceUtc, MaxBatchSize)
            .ConfigureAwait(false);

        if (entries.Count == 0)
        {
            PruneLocalLogs(syncState.LastSyncedUtc);
            return SyncOutcome.Idle;
        }

        var payload = entries
            .Select(MapEntry)
            .ToList();

        var accepted = await uploader.UploadBatchAsync(payload, ct).ConfigureAwait(false);
        if (accepted is null)
        {
            return SyncOutcome.Failed;
        }

        var maxTimestamp = entries.Max(x => x.Timestamp);
        if (maxTimestamp > syncState.LastSyncedUtc)
        {
            syncState.Save(maxTimestamp);
        }

        // Полный батч → почти наверняка есть ещё записи; prune откладываем до Idle.
        if (entries.Count >= MaxBatchSize)
        {
            return SyncOutcome.BacklogRemaining;
        }

        PruneLocalLogs(syncState.LastSyncedUtc);
        return SyncOutcome.Idle;
    }

    private static void PruneLocalLogs(DateTime minSyncedUtc)
    {
        var cutoff = DateTime.UtcNow.AddDays(-WorkerMaintenanceOptions.LogRetentionDays);
        _ = GlobalLogger.Instance.PruneLogFilesBeforeAsync(cutoff, minSyncedUtc);
    }

    private static WorkerLogEntryUploadDto MapEntry(LogFileEntry entry)
    {
        var level = entry.Level switch
        {
            DeskLinkAuditLogLevel.Error => "Error",
            DeskLinkAuditLogLevel.Warning => "Warning",
            DeskLinkAuditLogLevel.Debug => "Debug",
            _ => "Info"
        };

        return new WorkerLogEntryUploadDto(
            entry.Timestamp,
            level,
            string.IsNullOrWhiteSpace(entry.Prefix) ? "—" : entry.Prefix.Trim(),
            entry.Message ?? string.Empty,
            string.IsNullOrWhiteSpace(entry.TraceId) ? null : entry.TraceId.Trim(),
            entry.IsTampered);
    }

    internal enum SyncOutcome
    {
        Idle,
        BacklogRemaining,
        Failed
    }
}
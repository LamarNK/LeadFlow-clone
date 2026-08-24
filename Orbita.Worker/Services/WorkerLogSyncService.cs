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
    /// <summary>Пауза, когда локальный хвост пуст.</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);

    /// <summary>Пауза после ошибки API/сети.</summary>
    private static readonly TimeSpan ErrorRetryInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Безопасный размер батча (≤ типичного WorkerLogs:MaxBatchSize=500 на старых API).
    /// Полный батч → сразу следующий без IdleInterval (догон хвоста).
    /// Раньше 2000 ломало выгрузку, если API ещё с MaxBatchSize=500: вечный 400 и пустые логи на сайте.
    /// </summary>
    internal const int MaxBatchSize = 500;

    private DateTime _lastFailLogUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        syncState.Load();
        ClampCursorIfInFuture();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var outcome = await SyncOnceAsync(stoppingToken).ConfigureAwait(false);
                switch (outcome)
                {
                    case SyncOutcome.BacklogRemaining:
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
            catch (Exception ex)
            {
                await LogSyncIssueAsync(
                        $"Worker log sync: исключение при выгрузке: {ex.GetType().Name}: {ex.Message}",
                        DeskLinkAuditLogLevel.Warning)
                    .ConfigureAwait(false);

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
    /// Один проход. <see cref="SyncOutcome.BacklogRemaining"/> — полный батч ушёл, слать ещё сразу.
    /// </summary>
    internal async Task<SyncOutcome> SyncOnceAsync(CancellationToken ct)
    {
        ClampCursorIfInFuture();

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
            await LogSyncIssueAsync(
                    $"Worker log sync: batch отклонён/сеть (since={sinceUtc:o}, count={entries.Count}). Курсор не сдвинут.",
                    DeskLinkAuditLogLevel.Warning)
                .ConfigureAwait(false);
            return SyncOutcome.Failed;
        }

        var maxTimestamp = entries.Max(x => x.Timestamp);
        if (maxTimestamp > syncState.LastSyncedUtc)
        {
            syncState.Save(maxTimestamp);
        }

        if (entries.Count >= MaxBatchSize)
        {
            return SyncOutcome.BacklogRemaining;
        }

        PruneLocalLogs(syncState.LastSyncedUtc);
        return SyncOutcome.Idle;
    }

    /// <summary>
    /// Если lastSyncedUtc уехал в будущее (сбой часов / кривой state) — чтение даёт 0 записей навсегда.
    /// </summary>
    private void ClampCursorIfInFuture()
    {
        var now = DateTime.UtcNow;
        if (syncState.LastSyncedUtc <= now.AddMinutes(5))
        {
            return;
        }

        var resetTo = now.AddHours(-6);
        syncState.Save(resetTo);
        _ = LogSyncIssueAsync(
            $"Worker log sync: lastSyncedUtc был в будущем, сброшен на {resetTo:o}",
            DeskLinkAuditLogLevel.Warning);
    }

    private async Task LogSyncIssueAsync(string message, DeskLinkAuditLogLevel level)
    {
        // Не чаще раза в 5 минут — иначе при вечном fail зальём диск.
        var now = DateTime.UtcNow;
        if (now - _lastFailLogUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastFailLogUtc = now;
        try
        {
            await GlobalLogger.Instance.LogAsync(
                    message,
                    level,
                    memberName: nameof(WorkerLogSyncService),
                    filePath: "WorkerLogSyncService.cs")
                .ConfigureAwait(false);
        }
        catch
        {
            // never throw from diagnostics
        }
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

        var properties = WorkerLogPropertyAllowlist.ToTransportMap(
            LogEnvelopeParser.ParseContext(entry.Properties));
        return new WorkerLogEntryUploadDto(
            entry.Timestamp,
            level,
            string.IsNullOrWhiteSpace(entry.Prefix) ? "—" : entry.Prefix.Trim(),
            entry.Message ?? string.Empty,
            string.IsNullOrWhiteSpace(entry.TraceId) ? null : entry.TraceId.Trim(),
            entry.IsTampered,
            properties.Count == 0 ? null : properties);
    }

    internal enum SyncOutcome
    {
        Idle,
        BacklogRemaining,
        Failed
    }
}

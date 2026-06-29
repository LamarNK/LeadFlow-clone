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
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(60);
    private const int MaxBatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        syncState.Load();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // retry on next interval
            }

            await Task.Delay(SyncInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task SyncOnceAsync(CancellationToken ct)
    {
        var sinceUtc = syncState.LastSyncedUtc;
        var entries = await GlobalLogger.Instance
            .ReadEntriesNewerThanAsync(sinceUtc, MaxBatchSize)
            .ConfigureAwait(false);

        if (entries.Count == 0)
        {
            PruneLocalLogs(syncState.LastSyncedUtc);
            return;
        }

        var payload = entries
            .Select(MapEntry)
            .ToList();

        var accepted = await uploader.UploadBatchAsync(payload, ct).ConfigureAwait(false);
        if (accepted is null)
        {
            return;
        }

        var maxTimestamp = entries.Max(x => x.Timestamp);
        if (maxTimestamp > syncState.LastSyncedUtc)
        {
            syncState.Save(maxTimestamp);
        }

        PruneLocalLogs(syncState.LastSyncedUtc);
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
}
using LeadFlow.Core.Logging.Audit;
using Microsoft.Extensions.Hosting;

namespace Orbita.Worker.Services;

public sealed class WorkerOutboxRetryService(
    WorkerCandidateOutbox outbox,
    OrbitaApiClient apiClient,
    OrbitaCandidateDuplicateRepository dedupRepository) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var entries = await outbox.DequeueAsync(5, stoppingToken).ConfigureAwait(false);
                foreach (var entry in entries)
                {
                    if (entry.AttemptCount >= 20)
                    {
                        await outbox.RemoveAsync(entry.Id, stoppingToken).ConfigureAwait(false);
                        _ = GlobalLogger.Instance.LogAsync(
                            $"Outbox: отброшена пачка из {entry.Batch.Candidates.Count} откликов после 20 попыток.",
                            DeskLinkAuditLogLevel.Warning);
                        continue;
                    }

                    var result = await apiClient.SubmitCandidatesAsync(entry.Batch, stoppingToken)
                        .ConfigureAwait(false);
                    if (result is null)
                    {
                        await outbox.IncrementAttemptAsync(entry.Id, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    await RecordDedupAsync(entry.Batch, stoppingToken).ConfigureAwait(false);
                    await outbox.RemoveAsync(entry.Id, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Outbox retry failed: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning);
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RecordDedupAsync(
        Orbita.Contracts.WorkerCandidateBatchRequest batch,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in batch.Candidates)
        {
            await dedupRepository.RecordSeenAsync(
                    candidate.AccountId,
                    candidate.SourceResponseId,
                    NormalizePhone(candidate.PhoneRaw),
                    candidate.AvitoSubProfileId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string? NormalizePhone(string phoneRaw) =>
        string.IsNullOrWhiteSpace(phoneRaw) ? null : phoneRaw.Trim();
}
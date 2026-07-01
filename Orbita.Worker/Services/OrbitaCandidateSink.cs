using System.Collections.Concurrent;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

/// <summary>
/// Batches candidate publishes to reduce HTTP roundtrips.
/// Flushes on size threshold, time window, or explicit FlushAsync (e.g. on monitoring stop).
/// </summary>
public sealed class OrbitaCandidateSink(OrbitaApiClient apiClient) : INewCandidateSink, IAsyncDisposable
{
    private const int MaxBatchSize = 5;
    private static readonly TimeSpan FlushWindow = TimeSpan.FromMilliseconds(1500);

    private readonly ConcurrentQueue<WorkerCandidateDto> _queue = new();
    private readonly object _flushGate = new();
    private DateTime _lastFlushUtc = DateTime.MinValue;
    private volatile bool _disposed;

    public async Task<CandidatePublishResult> PublishAsync(
        CandidateResponse candidate,
        CancellationToken cancellationToken)
    {
        if (_disposed) return CandidatePublishResult.Pending();

        var dto = new WorkerCandidateDto(
            candidate.AccountId,
            candidate.AccountName,
            candidate.Source,
            candidate.SourceResponseId,
            candidate.FullName,
            candidate.Age,
            candidate.PhoneRaw,
            candidate.City,
            candidate.Vacancy,
            candidate.VacancyUrl,
            candidate.MessengerUrl,
            candidate.AvitoSubProfileId,
            candidate.RawText,
            candidate.ChatMessagesJson,
            candidate.CreatedAt);

        _queue.Enqueue(dto);

        if (ShouldFlushNow())
        {
            await FlushInternalAsync(cancellationToken).ConfigureAwait(false);
        }

        // Return Pending; real status comes back on flush for the item (caller usually doesn't block on it).
        // For immediate feedback on a single publish we could force flush small, but batching wins for bursts.
        return CandidatePublishResult.Pending();
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;
        await FlushInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool ShouldFlushNow()
    {
        if (_queue.Count >= MaxBatchSize) return true;
        if (DateTime.UtcNow - _lastFlushUtc >= FlushWindow && !_queue.IsEmpty) return true;
        return false;
    }

    private async Task FlushInternalAsync(CancellationToken ct)
    {
        if (_queue.IsEmpty) return;

        List<WorkerCandidateDto> batchDtos;
        lock (_flushGate)
        {
            if (_queue.IsEmpty) return;
            batchDtos = new List<WorkerCandidateDto>();
            while (_queue.TryDequeue(out var item) && batchDtos.Count < 100)
            {
                batchDtos.Add(item);
            }
            _lastFlushUtc = DateTime.UtcNow;
        }

        if (batchDtos.Count == 0) return;

        var batch = new WorkerCandidateBatchRequest(batchDtos);
        // Fire and forget status per item is not critical for sink (ingestion service reports back via result if needed).
        // We await to apply backpressure and respect cancellation.
        _ = await apiClient.SubmitCandidatesAsync(batch, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await FlushInternalAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
    }
}
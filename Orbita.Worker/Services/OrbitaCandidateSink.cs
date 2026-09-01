using System.Collections.Concurrent;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

/// <summary>
/// Batches candidate publishes to reduce HTTP roundtrips.
/// Flushes when 5 candidates are queued, 1.5s after the first item in an idle queue,
/// or on explicit FlushAsync (e.g. monitoring stop). Live POSTs are serialized
/// and contain at most <see cref="MaxBatchSize"/> candidates.
/// </summary>
public sealed class OrbitaCandidateSink(
    OrbitaApiClient apiClient,
    OrbitaCandidateDuplicateRepository dedupRepository,
    WorkerCandidateOutbox outbox,
    AvitoAvatarDownloader avatarDownloader) : INewCandidateSink, IAsyncDisposable
{
    private const int MaxBatchSize = 5;
    private static readonly TimeSpan FlushWindow = TimeSpan.FromMilliseconds(1500);

    private readonly ConcurrentQueue<CandidateResponse> _queue = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _schedulerLock = new();
    private CancellationTokenSource? _delayCts;
    private Task _delayLoop = Task.CompletedTask;
    private int _delayGeneration;
    private bool _delayPending;
    private volatile bool _disposed;

    public async Task<CandidatePublishResult> PublishAsync(
        CandidateResponse candidate,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return CandidatePublishResult.Pending();
        }

        _queue.Enqueue(candidate);
        ArmDelayedFlush();

        if (_queue.Count >= MaxBatchSize)
        {
            await FlushInternalAsync(drainAll: false, cancellationToken).ConfigureAwait(false);
        }

        return CandidatePublishResult.Pending();
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await StopDelayAsync().ConfigureAwait(false);
        await FlushInternalAsync(drainAll: true, cancellationToken).ConfigureAwait(false);
    }

    private void ArmDelayedFlush()
    {
        lock (_schedulerLock)
        {
            TryArmDelayedFlush_NoLock();
        }
    }

    private void TryArmDelayedFlush_NoLock()
    {
        if (_disposed || _delayPending || _queue.IsEmpty)
        {
            return;
        }

        StartDelay_NoLock();
    }

    private void StartDelay_NoLock()
    {
        _delayCts?.Dispose();
        _delayCts = new CancellationTokenSource();
        _delayPending = true;
        var generation = ++_delayGeneration;
        _delayLoop = RunDelayedFlushAsync(generation, _delayCts.Token);
    }

    private async Task StopDelayAsync()
    {
        Task pending;
        lock (_schedulerLock)
        {
            _delayGeneration++;
            _delayPending = false;
            pending = _delayLoop;
            try
            {
                _delayCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunDelayedFlushAsync(int generation, CancellationToken delayCt)
    {
        try
        {
            await Task.Delay(FlushWindow, delayCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_schedulerLock)
            {
                if (generation == _delayGeneration)
                {
                    _delayPending = false;
                }
            }
        }

        if (!delayCt.IsCancellationRequested && !_disposed)
        {
            try
            {
                await FlushInternalAsync(drainAll: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Background send: unsent batch is already in outbox.
            }
        }

        lock (_schedulerLock)
        {
            if (generation != _delayGeneration)
            {
                return;
            }

            _delayPending = false;
            TryArmDelayedFlush_NoLock();
        }
    }

    private async Task FlushInternalAsync(bool drainAll, CancellationToken ct)
    {
        await _sendGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            while (TryDequeueBatch(out var candidates))
            {
                var submitCt = drainAll ? CancellationToken.None : ct;
                var batchDtos = await Task.WhenAll(candidates.Select(candidate => ToDtoAsync(candidate, submitCt)))
                    .ConfigureAwait(false);
                var batch = new WorkerCandidateBatchRequest(batchDtos);

                var submitted = false;
                try
                {
                    var result = await apiClient.SubmitCandidatesAsync(batch, submitCt).ConfigureAwait(false);
                    if (result is null)
                    {
                        await outbox.EnqueueAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        submitted = true;
                        await RecordDedupAsync(batchDtos, submitCt).ConfigureAwait(false);
                    }
                }
                catch
                {
                    if (!submitted)
                    {
                        await outbox.EnqueueAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    }

                    if (!drainAll)
                    {
                        throw;
                    }
                }

                if (!drainAll && ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }

        ArmDelayedFlush();
    }

    private bool TryDequeueBatch(out List<CandidateResponse> candidates)
    {
        candidates = new List<CandidateResponse>(MaxBatchSize);
        while (candidates.Count < MaxBatchSize && _queue.TryDequeue(out var item))
        {
            candidates.Add(item);
        }

        return candidates.Count > 0;
    }

    private async Task RecordDedupAsync(IReadOnlyList<WorkerCandidateDto> batch, CancellationToken ct)
    {
        foreach (var candidate in batch)
        {
            await dedupRepository.RecordSeenAsync(
                    candidate.AccountId,
                    candidate.SourceResponseId,
                    NormalizePhone(candidate.PhoneRaw),
                    candidate.AvitoSubProfileId,
                    ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<WorkerCandidateDto> ToDtoAsync(CandidateResponse candidate, CancellationToken ct)
    {
        var avatar = await avatarDownloader.DownloadAsync(candidate.AvatarUrl, ct).ConfigureAwait(false);
        return new WorkerCandidateDto(
            candidate.AccountId,
            candidate.AccountName,
            candidate.Source,
            candidate.SourceResponseId,
            candidate.CardFingerprint,
            candidate.FullName,
            candidate.Age,
            string.IsNullOrWhiteSpace(candidate.Gender) ? null : candidate.Gender,
            candidate.PhoneRaw,
            candidate.City,
            candidate.Vacancy,
            candidate.VacancyUrl,
            candidate.MessengerUrl,
            candidate.AvitoSubProfileId,
            candidate.RawText,
            candidate.ChatMessagesJson,
            candidate.CreatedAt,
            candidate.AvitoSubProfileName,
            candidate.CollectedAt,
            candidate.PhoneMetricKind ?? string.Empty,
            candidate.PreviousPhoneRaw,
            candidate.PreviousPhoneNormalized,
            candidate.PhoneUnchangedHours,
            candidate.PhoneChangedAtUtc,
            avatar?.ContentType,
            avatar is null ? null : Convert.ToBase64String(avatar.Bytes),
            candidate.Citizenship);
    }

    private static string? NormalizePhone(string phoneRaw) =>
        string.IsNullOrWhiteSpace(phoneRaw) ? null : phoneRaw.Trim();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopDelayAsync().ConfigureAwait(false);
            await FlushInternalAsync(drainAll: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }

        _sendGate.Dispose();
        lock (_schedulerLock)
        {
            _delayCts?.Dispose();
            _delayCts = null;
        }
    }
}

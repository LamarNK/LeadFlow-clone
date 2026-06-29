using System.Collections.Concurrent;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;
using Orbita.Worker;

namespace Orbita.Worker.Services;

/// <summary>
/// Batches worker events before sending to Orbita API.
/// </summary>
public sealed class WorkerEventSink(OrbitaApiClient apiClient, WorkerCredentials credentials) : IWorkerEventSink, IAsyncDisposable
{
    private const int MaxBatchSize = 10;
    private static readonly TimeSpan FlushWindow = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<WorkerEventDto> _queue = new();
    private readonly object _flushGate = new();
    private DateTime _lastFlushUtc = DateTime.MinValue;
    private volatile bool _disposed;

    public async Task PublishAsync(
        Guid? accountId,
        string level,
        string message,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _queue.Enqueue(new WorkerEventDto(
            accountId,
            level.Trim(),
            message.Trim(),
            string.IsNullOrWhiteSpace(details) ? null : details.Trim(),
            DateTime.UtcNow));

        if (ShouldFlushNow())
        {
            await FlushInternalAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        FlushInternalAsync(cancellationToken);

    private bool ShouldFlushNow()
    {
        if (_queue.Count >= MaxBatchSize)
        {
            return true;
        }

        return DateTime.UtcNow - _lastFlushUtc >= FlushWindow && !_queue.IsEmpty;
    }

    private async Task FlushInternalAsync(CancellationToken ct)
    {
        if (_queue.IsEmpty || credentials.WorkerId is null)
        {
            return;
        }

        List<WorkerEventDto> batch;
        lock (_flushGate)
        {
            if (_queue.IsEmpty || credentials.WorkerId is null)
            {
                return;
            }

            batch = [];
            while (_queue.TryDequeue(out var item) && batch.Count < 100)
            {
                batch.Add(item);
            }

            _lastFlushUtc = DateTime.UtcNow;
        }

        if (batch.Count == 0)
        {
            return;
        }

        await apiClient.SendEventsAsync(
            new WorkerEventBatchRequest(credentials.WorkerId.Value, batch),
            ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

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
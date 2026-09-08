using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class ResponseCacheInvalidator(
    IOrbitaQueryCache queryCache,
    ILogger<ResponseCacheInvalidator> logger)
{
    private static readonly PanelChangeKind[] ResponseChanges = [PanelChangeKind.Responses];
    private readonly object _sync = new();
    private readonly HashSet<Guid?> _pendingOfficeIds = [];
    private int _batchDepth;

    public Batch BeginBatch()
    {
        lock (_sync)
        {
            _batchDepth++;
        }

        return new Batch(this);
    }

    public async Task InvalidateAsync(Guid? officeId)
    {
        lock (_sync)
        {
            if (_batchDepth > 0)
            {
                _pendingOfficeIds.Add(officeId);
                return;
            }
        }

        await InvalidateCoreAsync(officeId);
    }

    private async ValueTask EndBatchAsync()
    {
        Guid?[] pending;
        lock (_sync)
        {
            if (_batchDepth == 0)
            {
                return;
            }

            _batchDepth--;
            if (_batchDepth > 0)
            {
                return;
            }

            pending = _pendingOfficeIds.ToArray();
            _pendingOfficeIds.Clear();
        }

        foreach (var officeId in pending)
        {
            await InvalidateCoreAsync(officeId);
        }
    }

    private async Task InvalidateCoreAsync(Guid? officeId)
    {
        try
        {
            await queryCache.InvalidateAsync(ResponseChanges, officeId);
        }
        catch (Exception exception)
        {
            // A successful response delivery must not be rolled back because Redis is unavailable.
            logger.LogWarning(exception, "Failed to invalidate response caches for office {OfficeId}.", officeId);
        }
    }

    public sealed class Batch(ResponseCacheInvalidator owner) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask FlushAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0
                ? owner.EndBatchAsync()
                : ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => FlushAsync();
    }
}

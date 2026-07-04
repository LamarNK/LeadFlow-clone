using LeadFlow.Core.Logging.Audit;

namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Сливает частые запросы snapshot во время обработки аккаунта и даёт немедленный push при завершении.
/// </summary>
public sealed class DebouncedWorkerTelemetryPusher(
    IWorkerTelemetrySink sink,
    TimeSpan? debounceDelay = null)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _debounceCts;
    private readonly TimeSpan _debounceDelay = debounceDelay ?? TimeSpan.FromSeconds(8);

    public void RequestDebouncedPush(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _debounceCts.Token;
            _ = RunDebouncedPushAsync(token);
        }
    }

    public async Task PushNowAsync(CancellationToken cancellationToken)
    {
        CancelPendingDebounced();
        await PushSafeAsync(cancellationToken).ConfigureAwait(false);
    }

    public void CancelPendingDebounced()
    {
        lock (_gate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
    }

    private async Task RunDebouncedPushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_debounceDelay, cancellationToken).ConfigureAwait(false);
            await PushSafeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PushSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await sink.PushSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Не удалось отправить snapshot телеметрии: {ex.Message}",
                DeskLinkAuditLogLevel.Warning);
        }
    }
}
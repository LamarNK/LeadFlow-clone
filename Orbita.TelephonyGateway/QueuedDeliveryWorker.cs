namespace Orbita.TelephonyGateway;

public sealed class QueuedDeliveryWorker(
    EncryptedFileQueue queue,
    GatewayForwarder forwarder,
    GatewayOptions options,
    TimeProvider timeProvider,
    ILogger<QueuedDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollIntervalSeconds), timeProvider);
        do
        {
            try
            {
                await DeliverBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Telephony queue delivery cycle failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task DeliverBatchAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var jobs = await queue.GetDueAsync(now, options.DeliveryBatchSize, ct);
        foreach (var job in jobs)
        {
            var result = await forwarder.ForwardAsync(job, ct);
            if (result.IsSuccess)
            {
                await queue.CompleteAsync(job.Id, ct);
                continue;
            }

            var attempt = job.AttemptCount + 1;
            if (!result.ShouldRetry || attempt >= options.MaxDeliveryAttempts)
            {
                await queue.MoveToDeadLetterAsync(job.Id, ct);
                logger.LogWarning(
                    "Telephony event {EventId} for {Provider} moved to dead letter after {AttemptCount} attempts with status {StatusCode}.",
                    job.Id,
                    job.Provider,
                    attempt,
                    (int)result.StatusCode);
                continue;
            }

            var exponent = Math.Min(attempt - 1, 16);
            var delaySeconds = Math.Min(
                options.RetryMaxSeconds,
                options.RetryBaseSeconds * Math.Pow(2, exponent));
            var jitter = Random.Shared.NextDouble() * Math.Min(5, options.RetryBaseSeconds);
            await queue.RescheduleAsync(
                job with
                {
                    AttemptCount = attempt,
                    NextAttemptAtUtc = now.AddSeconds(delaySeconds + jitter)
                },
                ct);
        }
    }
}

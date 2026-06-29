namespace LeadFlow.Core.Services.Worker;

public interface IWorkerEventSink
{
    Task PublishAsync(
        Guid? accountId,
        string level,
        string message,
        string? details = null,
        CancellationToken cancellationToken = default);

    Task FlushAsync(CancellationToken cancellationToken = default);
}
namespace LeadFlow.Core.Services.Worker;

public sealed class NullWorkerEventSink : IWorkerEventSink
{
    public Task PublishAsync(
        Guid? accountId,
        string level,
        string message,
        string? details = null,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
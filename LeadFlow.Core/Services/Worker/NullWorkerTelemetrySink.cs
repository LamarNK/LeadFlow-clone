namespace LeadFlow.Core.Services.Worker;

public sealed class NullWorkerTelemetrySink : IWorkerTelemetrySink
{
    public Task PushSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
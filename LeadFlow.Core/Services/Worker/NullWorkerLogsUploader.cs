using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

public sealed class NullWorkerLogsUploader : IWorkerLogsUploader
{
    public Task<int?> UploadBatchAsync(IReadOnlyList<WorkerLogEntryUploadDto> entries, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(0);
}
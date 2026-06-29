using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

public interface IWorkerLogsUploader
{
    Task<int?> UploadBatchAsync(IReadOnlyList<WorkerLogEntryUploadDto> entries, CancellationToken cancellationToken = default);
}
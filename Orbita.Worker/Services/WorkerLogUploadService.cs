using System.Net.Http.Json;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class WorkerLogUploadService(
    OrbitaApiClient apiClient,
    WorkerCredentials credentials) : IWorkerLogsUploader
{
    public async Task<int?> UploadBatchAsync(
        IReadOnlyList<WorkerLogEntryUploadDto> entries,
        CancellationToken cancellationToken = default)
    {
        if (credentials.WorkerId is null || entries.Count == 0)
        {
            return 0;
        }

        return await apiClient.UploadLogsBatchAsync(new WorkerLogsBatchRequest(entries), cancellationToken)
            .ConfigureAwait(false);
    }
}
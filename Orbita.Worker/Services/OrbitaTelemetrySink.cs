using LeadFlow.Core.Services.Worker;

namespace Orbita.Worker.Services;

public sealed class OrbitaTelemetrySink(
    OrbitaApiClient apiClient,
    WorkerTelemetryCollector telemetryCollector,
    WorkerCredentials credentials) : IWorkerTelemetrySink
{
    public async Task PushSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (credentials.WorkerId is null)
        {
            return;
        }

        var config = await apiClient.GetConfigAsync(cancellationToken).ConfigureAwait(false);
        if (config is null || config.Accounts.Count == 0)
        {
            return;
        }

        var snapshot = await telemetryCollector.BuildSnapshotAsync(config, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        await apiClient.SendSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }
}
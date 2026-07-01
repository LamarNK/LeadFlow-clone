using System.Net.Http.Headers;
using System.Net.Http.Json;
using LeadFlow.Core.Logging.Audit;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class OrbitaApiClient
{
    private readonly HttpClient _http;
    private readonly WorkerCredentials _credentials;

    public OrbitaApiClient(HttpClient http, WorkerCredentials credentials)
    {
        _http = http;
        _credentials = credentials;
        _http.BaseAddress = new Uri(WorkerSetupConstants.ApiBaseUrl.TrimEnd('/') + "/");
    }

    public void ApplyAuth(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.ApiKey);
    }

    public async Task<WorkerConfigDto?> GetConfigAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/workers/config");
        ApplyAuth(request);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker config request failed with HTTP {(int)response.StatusCode}.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(GetConfigAsync),
                filePath: "OrbitaApiClient.cs",
                properties: new Dictionary<string, object?>
                {
                    ["http.statusCode"] = (int)response.StatusCode,
                    ["http.path"] = "api/v1/workers/config"
                });
            return null;
        }

        return await response.Content.ReadFromJsonAsync<WorkerConfigDto>(ct).ConfigureAwait(false);
    }

    public async Task<bool> SyncAccountsAsync(WorkerAccountSyncRequest syncRequest, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/accounts/sync");
        ApplyAuth(request);
        request.Content = JsonContent.Create(syncRequest);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async Task<WorkerCandidateIngestionResultDto?> SubmitCandidatesAsync(
        WorkerCandidateBatchRequest batch,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/candidates");
        ApplyAuth(request);
        request.Content = JsonContent.Create(batch);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<WorkerCandidateIngestionResultDto>(ct).ConfigureAwait(false);
    }

    public async Task SendHeartbeatAsync(WorkerHeartbeatRequest heartbeat, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/heartbeat");
        ApplyAuth(request);
        request.Content = JsonContent.Create(heartbeat);
        await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    public async Task SendSnapshotAsync(WorkerSnapshotRequest snapshot, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/telemetry/snapshot");
        ApplyAuth(request);
        request.Content = JsonContent.Create(snapshot);
        await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    public async Task SendEventsAsync(WorkerEventBatchRequest events, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/telemetry/events");
        ApplyAuth(request);
        request.Content = JsonContent.Create(events);
        await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<WorkerDiagnosticUploadResponse?> UploadDiagnosticAsync(
        HttpContent content,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/diagnostics/upload")
        {
            Content = content
        };
        ApplyAuth(request);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<WorkerDiagnosticUploadResponse>(ct).ConfigureAwait(false);
    }

    public async Task<int?> UploadLogsBatchAsync(WorkerLogsBatchRequest batch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/logs/batch");
        ApplyAuth(request);
        request.Content = JsonContent.Create(batch);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<WorkerLogsUploadResponse>(ct).ConfigureAwait(false);
        return payload?.Accepted ?? 0;
    }

    public async Task<WorkerUpdateCheckResponse?> CheckForUpdateAsync(string currentVersion, CancellationToken ct)
    {
        var url = $"api/v1/workers/updates/check?currentVersion={Uri.EscapeDataString(currentVersion)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<WorkerUpdateCheckResponse>(ct).ConfigureAwait(false);
    }

    public async Task<(bool Success, string? Error)> DownloadUpdateAsync(
        string downloadPath,
        string targetPath,
        CancellationToken ct)
    {
        var relativePath = downloadPath.TrimStart('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        ApplyAuth(request);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (false, $"HTTP {(int)response.StatusCode}");
        }

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(targetPath);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        return (true, null);
    }

    private sealed record WorkerLogsUploadResponse(int Accepted);
}
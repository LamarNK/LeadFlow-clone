using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LeadFlow.Core.Logging.Audit;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class OrbitaApiClient
{
    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly WorkerCredentials _credentials;

    public string? LastConfigError { get; private set; }

    public bool LastConfigWasUnauthorized { get; private set; }

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
            LastConfigWasUnauthorized = WorkerConnectionErrors.IsUnauthorizedFailure((int)response.StatusCode);
            LastConfigError = WorkerConnectionErrors.FormatConfigFailure((int)response.StatusCode)
                ?? $"HTTP {(int)response.StatusCode}";
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

        LastConfigError = null;
        LastConfigWasUnauthorized = false;
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

    public async Task<bool> ReportProviderCheckAsync(
        ReportWorkerBrowserProviderCheckRequest report,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/provider-checks");
        ApplyAuth(request);
        request.Content = JsonContent.Create(report);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async Task<WorkerMonitoringStatsDto?> GetMonitoringStatsAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/workers/monitoring-stats");
        ApplyAuth(request);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<WorkerMonitoringStatsDto>(ct).ConfigureAwait(false);
    }

    public async Task<WorkerCandidateLookupResponse?> LookupCandidatesAsync(
        WorkerCandidateLookupRequest request,
        CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/candidates/lookup");
        ApplyAuth(httpRequest);
        httpRequest.Content = JsonContent.Create(request);
        var response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<WorkerCandidateLookupResponse>(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkerPendingChatMessageDto>> GetPendingChatMessagesAsync(
        Guid accountId,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/v1/workers/pending-chat-messages?accountId={accountId:D}");
        ApplyAuth(request);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        return await response.Content.ReadFromJsonAsync<List<WorkerPendingChatMessageDto>>(JsonReadOptions, ct)
                   .ConfigureAwait(false)
               ?? [];
    }

    public async Task<bool> AckOutboundChatAsync(
        IReadOnlyList<Guid> sentIds,
        CancellationToken ct)
    {
        if (sentIds.Count == 0)
        {
            return true;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/outbound-chat/ack");
        ApplyAuth(request);
        request.Content = JsonContent.Create(new WorkerOutboundChatAckRequest(sentIds));
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ClaimOutboundChatAsync(Guid messageId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/outbound-chat/claim")
        {
            Content = JsonContent.Create(new WorkerOutboundChatClaimRequest(messageId))
        };
        ApplyAuth(request);
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

    public async Task<(bool Success, string? Error)> UpdateCaptchaSessionStatusAsync(
        UpdateCaptchaSessionStatusRequest request,
        CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/captcha-sessions/status");
        ApplyAuth(httpRequest);
        httpRequest.Content = JsonContent.Create(request);
        var response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (false, string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)response.StatusCode}" : body);
    }

    public async Task<CaptchaSessionDto?> GetCaptchaSessionAsync(Guid sessionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/workers/captcha-sessions/{sessionId:D}");
        ApplyAuth(request);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<CaptchaSessionDto>(cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<bool> IsBrowserMonitorSessionAliveAsync(Guid sessionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/workers/browser-monitor-sessions/{sessionId:D}/alive");
        ApplyAuth(request);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async Task CompleteLocalChromeLoginAsync(Guid sessionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/local-chrome-login/complete");
        ApplyAuth(request);
        request.Content = JsonContent.Create(new CompleteLocalChromeLoginRequest(sessionId));
        await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    public async Task SendHeartbeatAsync(WorkerHeartbeatRequest heartbeat, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/heartbeat");
        ApplyAuth(request);
        request.Content = JsonContent.Create(heartbeat);
        await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    public async Task SendActivityAsync(WorkerActivityRequest activity, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/activity");
        ApplyAuth(request);
        request.Content = JsonContent.Create(activity);
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

    public async Task<bool> SendMonitoringRunsAsync(MonitoringRunBatchRequest batch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/telemetry/monitoring-runs");
        ApplyAuth(request);
        request.Content = JsonContent.Create(batch);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
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
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker logs/batch failed HTTP {(int)response.StatusCode}.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(UploadLogsBatchAsync),
                filePath: "OrbitaApiClient.cs",
                properties: new Dictionary<string, object?>
                {
                    ["http.statusCode"] = (int)response.StatusCode,
                    ["http.path"] = "api/v1/workers/logs/batch",
                    ["batch.count"] = batch.Entries?.Count ?? 0
                });
            return null;
        }

        // camelCase "accepted" с API — без case-insensitive десериализация даёт Accepted=0.
        var payload = await response.Content
            .ReadFromJsonAsync<WorkerLogsUploadResponse>(JsonReadOptions, ct)
            .ConfigureAwait(false);
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
        long? expectedFileSize = null,
        CancellationToken ct = default)
    {
        var relativePath = downloadPath.TrimStart('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        ApplyAuth(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (false, $"HTTP {(int)response.StatusCode}");
        }

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(targetPath);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);

        var length = output.Length;
        if (length == 0)
        {
            return (false, "Сервер вернул пустой файл.");
        }

        if (response.Content.Headers.ContentLength is > 0 && length != response.Content.Headers.ContentLength.Value)
        {
            return (false, "Размер скачанного файла не совпал с ответом сервера.");
        }

        if (expectedFileSize is > 0 && length != expectedFileSize.Value)
        {
            return (false, $"Ожидался файл {expectedFileSize.Value} байт, получено {length} байт.");
        }

        return (true, null);
    }

    private sealed record WorkerLogsUploadResponse(int Accepted);
}

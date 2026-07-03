using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class OrbitaApiClient(HttpClient http, AuthSession session, IOptions<DesignPreviewOptions> previewOptions)
{
    private const string InvalidApiSessionError =
        "Сессия недействительна. Выйдите из панели и войдите снова.";

    private readonly DesignPreviewOptions _preview = previewOptions.Value;

    private bool CanUseAuthenticatedApi =>
        !_preview.Enabled && IsJwtToken(session.Token);

    private static bool IsJwtToken(string? token) =>
        !string.IsNullOrWhiteSpace(token)
        && !string.Equals(token, AuthSession.DesignPreviewToken, StringComparison.Ordinal)
        && token.Count(c => c == '.') >= 2;

    public Task<LoginResponse?> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            if (string.Equals(email, _preview.Email, StringComparison.OrdinalIgnoreCase)
                && password == _preview.Password)
            {
                return Task.FromResult<LoginResponse?>(new LoginResponse(AuthSession.DesignPreviewToken, email));
            }

            return Task.FromResult<LoginResponse?>(null);
        }

        return LoginViaApiAsync(email, password, ct);
    }

    private async Task<LoginResponse?> LoginViaApiAsync(string email, string password, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email, password })
        };
        using var response = await SendAsyncSafe(request, HttpCompletionOption.ResponseContentRead, ct);
        if (response is null)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<LoginResponse>(ct);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Token))
        {
            return null;
        }

        return payload;
    }

    public Task<GlobalDashboardSummary?> GetSummaryAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<GlobalDashboardSummary?>(DesignPreviewData.Summary)
            : GetAsync<GlobalDashboardSummary>("api/v1/dashboard/summary", ct);

    public Task<IReadOnlyList<WorkerListItem>?> GetWorkersAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<WorkerListItem>?>(DesignPreviewData.Workers)
            : GetAsync<IReadOnlyList<WorkerListItem>>("api/v1/workers", ct);

    public Task<WorkerDetail?> GetWorkerAsync(Guid id, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult(DesignPreviewData.GetWorker(id))
            : GetAsync<WorkerDetail>($"api/v1/workers/{id}", ct);

    public Task<IReadOnlyList<WorkerAccountDto>?> GetWorkerAccountsAsync(Guid id, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<WorkerAccountDto>?>(DesignPreviewData.GetAccounts(id))
            : GetAsync<IReadOnlyList<WorkerAccountDto>>($"api/v1/workers/{id}/accounts", ct);

    public Task<IReadOnlyList<WorkerEventListItem>?> GetEventsAsync(
        Guid? workerId = null,
        int limit = 100,
        DateTime? sinceUtc = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            var events = DesignPreviewData.Events;
            if (workerId.HasValue)
            {
                events = events.Where(e => e.WorkerId == workerId.Value).ToList();
            }

            if (sinceUtc.HasValue)
            {
                events = events.Where(e => e.CreatedAtUtc >= sinceUtc.Value).ToList();
            }

            return Task.FromResult<IReadOnlyList<WorkerEventListItem>?>(
                events.OrderByDescending(e => e.CreatedAtUtc).Take(limit).ToList());
        }

        var parts = new List<string> { $"limit={limit}" };
        if (workerId.HasValue)
        {
            parts.Add($"workerId={workerId}");
        }

        if (sinceUtc.HasValue)
        {
            parts.Add($"since={Uri.EscapeDataString(sinceUtc.Value.ToString("o"))}");
        }

        return GetAsync<IReadOnlyList<WorkerEventListItem>>($"api/v1/events?{string.Join('&', parts)}", ct);
    }

    public async Task<(Stream? Stream, string? ContentType)> GetDiagnosticImageAsync(Guid id, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/dashboard/diagnostics/{id}/image");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            return (null, null);
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var buffer = await response.Content.ReadAsByteArrayAsync(ct);
        return buffer.Length == 0 ? (null, contentType) : (new MemoryStream(buffer), contentType);
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(ct);
    }

    private async Task<HttpResponseMessage?> SendAuthenticatedAsync(
        HttpRequestMessage request,
        CancellationToken ct,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        if (!CanUseAuthenticatedApi)
        {
            return null;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token!);
        return await SendAsyncSafe(request, completionOption, ct);
    }

    private async Task<HttpResponseMessage?> SendAsyncSafe(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, completionOption, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<PanelUserDto>?> GetPanelUsersAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PanelUserDto>>("api/v1/admin/users", ct);

    public Task<ServiceLogsPageDto?> GetServiceLogsAsync(
        string? q,
        string? level,
        string? service,
        DateTime? date,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return Task.FromResult<ServiceLogsPageDto?>(
                DesignPreviewData.BuildServiceLogsPage(q, level, service, date, page, pageSize));
        }

        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            query.Add($"q={Uri.EscapeDataString(q)}");
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            query.Add($"level={Uri.EscapeDataString(level)}");
        }

        if (!string.IsNullOrWhiteSpace(service))
        {
            query.Add($"service={Uri.EscapeDataString(service)}");
        }

        if (date.HasValue)
        {
            query.Add($"date={date.Value:yyyy-MM-dd}");
        }

        query.Add($"page={page}");
        query.Add($"pageSize={pageSize}");
        var url = "api/v1/admin/logs?" + string.Join("&", query);
        return GetAsync<ServiceLogsPageDto>(url, ct);
    }

    public Task<WorkerLogsPageDto?> GetWorkerLogsAsync(
        Guid workerId,
        string? q,
        string? level,
        DateTime? date,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return Task.FromResult<WorkerLogsPageDto?>(
                DesignPreviewData.BuildWorkerLogsPage(workerId, q, level, date, page, pageSize));
        }

        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            query.Add($"q={Uri.EscapeDataString(q)}");
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            query.Add($"level={Uri.EscapeDataString(level)}");
        }

        if (date.HasValue)
        {
            query.Add($"date={date.Value:yyyy-MM-dd}");
        }

        query.Add($"page={page}");
        query.Add($"pageSize={pageSize}");
        var url = $"api/v1/admin/workers/{workerId}/logs?" + string.Join("&", query);
        return GetAsync<WorkerLogsPageDto>(url, ct);
    }

    public Task<IReadOnlyList<OfficeDto>?> GetOfficesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<OfficeDto>>("api/v1/admin/offices", ct);

    public Task<OfficeDetailDto?> GetOfficeAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<OfficeDetailDto>($"api/v1/admin/offices/{id}", ct);

    public async Task<(OfficeDetailDto? Office, string? Error)> CreateOfficeAsync(string name, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/admin/offices");
        request.Content = JsonContent.Create(new CreateOfficeRequest(name));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var office = await response.Content.ReadFromJsonAsync<OfficeDetailDto>(ct);
        return office is null ? (null, "Не удалось прочитать ответ API.") : (office, null);
    }

    public async Task<(OfficeDetailDto? Office, string? Error)> UpdateOfficeAsync(
        Guid id,
        string name,
        bool isEnabled,
        bool bitrixTransmissionEnabled,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/admin/offices/{id}");
        request.Content = JsonContent.Create(new UpdateOfficeRequest(name, isEnabled, bitrixTransmissionEnabled));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var office = await response.Content.ReadFromJsonAsync<OfficeDetailDto>(ct);
        return office is null ? (null, "Не удалось прочитать ответ API.") : (office, null);
    }

    public async Task<(RotateOfficeRegistrationSecretResponse? Result, string? Error)> RotateOfficeRegistrationSecretAsync(
        Guid id,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/admin/offices/{id}/rotate-registration-secret");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var result = await response.Content.ReadFromJsonAsync<RotateOfficeRegistrationSecretResponse>(ct);
        return result is null ? (null, "Не удалось прочитать ответ API.") : (result, null);
    }

    public async Task<(bool Success, string? Error)> CreatePanelUserAsync(
        string email,
        string password,
        string role,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/admin/users");
        request.Content = JsonContent.Create(new CreatePanelUserRequest(email, password, role, officeId));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> DeletePanelUserAsync(string userId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/admin/users/{userId}");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> ResetPanelUserPasswordAsync(
        string userId,
        string password,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/admin/users/{userId}/password");
        request.Content = JsonContent.Create(new ResetPanelUserPasswordRequest(password));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> UpdatePanelUserRoleAsync(
        string userId,
        string role,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/admin/users/{userId}/role");
        request.Content = JsonContent.Create(new UpdatePanelUserRoleRequest(role));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> LockPanelUserAsync(string userId, CancellationToken ct = default) =>
        await PostAdminActionAsync($"api/v1/admin/users/{userId}/lock", ct);

    public async Task<(bool Success, string? Error)> UnlockPanelUserAsync(string userId, CancellationToken ct = default) =>
        await PostAdminActionAsync($"api/v1/admin/users/{userId}/unlock", ct);

    public async Task<(bool Success, string? Error)> RevokePanelUserSessionsAsync(string userId, CancellationToken ct = default) =>
        await PostAdminActionAsync($"api/v1/admin/users/{userId}/revoke-sessions", ct);

    public Task<IReadOnlyList<AdminWorkerListItemDto>?> GetAdminWorkersAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AdminWorkerListItemDto>>("api/v1/admin/workers", ct);

    public Task<WorkerRegistrationInfoDto?> GetWorkerRegistrationInfoAsync(CancellationToken ct = default) =>
        GetAsync<WorkerRegistrationInfoDto>("api/v1/admin/workers/registration", ct);

    public async Task<(bool Success, string? Error)> RenameAdminWorkerAsync(
        Guid workerId,
        string displayName,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/admin/workers/{workerId}");
        request.Content = JsonContent.Create(new UpdateAdminWorkerRequest(displayName));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> SetAdminWorkerEnabledAsync(
        Guid workerId,
        bool enabled,
        CancellationToken ct = default) =>
        await PostAdminActionAsync($"api/v1/admin/workers/{workerId}/{(enabled ? "enable" : "disable")}", ct);

    public async Task<(bool Success, string? Error)> UpdatePanelUserOfficeAsync(
        string userId,
        Guid? officeId,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/admin/users/{userId}/office");
        request.Content = JsonContent.Create(new UpdatePanelUserOfficeRequest(officeId));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(CreateWorkerResponse? Result, string? Error)> CreateWorkerAsync(
        string displayName,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/panel/workers/create");
        request.Content = JsonContent.Create(new CreateWorkerRequest(displayName, officeId));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var result = await response.Content.ReadFromJsonAsync<CreateWorkerResponse>(ct);
        return result is null ? (null, "Не удалось прочитать ответ API.") : (result, null);
    }

    public async Task<(bool Success, string? Error)> SetWorkerEnabledAsync(
        Guid workerId,
        bool enabled,
        CancellationToken ct = default) =>
        await PostPanelActionAsync($"api/v1/panel/workers/{workerId}/{(enabled ? "enable" : "disable")}", ct);

    public async Task<(bool Success, string? Error)> DeleteWorkerAsync(Guid workerId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/panel/workers/{workerId}");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(RotateWorkerApiKeyResponse? Result, string? Error)> RotateWorkerKeyAsync(
        Guid workerId,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/panel/workers/{workerId}/rotate-key");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var result = await response.Content.ReadFromJsonAsync<RotateWorkerApiKeyResponse>(ct);
        return result is null ? (null, "Не удалось прочитать ответ API.") : (result, null);
    }

    public Task<WorkerConfigDto?> GetWorkerConfigAsync(Guid workerId, CancellationToken ct = default) =>
        GetAsync<WorkerConfigDto>($"api/v1/workers/{workerId}/config", ct);

    public async Task<(bool Success, string? Error)> UpdateWorkerSettingsAsync(
        Guid workerId,
        int maxConcurrentAccounts,
        string? adsPowerApiBaseUrl,
        string? adsPowerApiKey,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/workers/{workerId}/settings");
        request.Content = JsonContent.Create(new UpdateWorkerSettingsRequest(
            maxConcurrentAccounts,
            adsPowerApiBaseUrl,
            adsPowerApiKey));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> SendWorkerCommandAsync(
        Guid workerId,
        string command,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/workers/{workerId}/commands");
        request.Content = JsonContent.Create(new WorkerCommandRequest(command));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> RequestSubProfilesRefreshAsync(
        Guid workerId,
        Guid accountId,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (true, null);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/workers/{workerId}/accounts/{accountId}/refresh-subprofiles");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> UpdateSubProfileEnabledAsync(
        Guid workerId,
        Guid accountId,
        string subProfileId,
        bool isEnabledInPanel,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (true, null);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"api/v1/workers/{workerId}/accounts/{accountId}/subprofiles/{Uri.EscapeDataString(subProfileId)}");
        request.Content = JsonContent.Create(new UpdateWorkerSubProfileRequest(isEnabledInPanel));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> DismissEventAsync(Guid eventId, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (true, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/panel/events/{eventId}/dismiss");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> UpdateWorkerAccountAsync(
        Guid workerId,
        Guid accountId,
        bool isEnabledInPanel,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/workers/{workerId}/accounts/{accountId}");
        request.Content = JsonContent.Create(new UpdateWorkerAccountRequest(isEnabledInPanel));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(RotateWorkerApiKeyResponse? Result, string? Error)> RotateWorkerApiKeyAsync(
        Guid workerId,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/admin/workers/{workerId}/rotate-key");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var result = await response.Content.ReadFromJsonAsync<RotateWorkerApiKeyResponse>(ct);
        return result is null ? (null, "Не удалось прочитать ответ API.") : (result, null);
    }

    public Task<PanelAuditPageDto?> GetPanelAuditAsync(
        string? q,
        string? action,
        DateTime? date,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return Task.FromResult<PanelAuditPageDto?>(
                DesignPreviewData.BuildPanelAuditPage(q, action, date, page, pageSize));
        }

        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            query.Add($"q={Uri.EscapeDataString(q)}");
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query.Add($"action={Uri.EscapeDataString(action)}");
        }

        if (date.HasValue)
        {
            query.Add($"date={date.Value:yyyy-MM-dd}");
        }

        query.Add($"page={page}");
        query.Add($"pageSize={pageSize}");
        return GetAsync<PanelAuditPageDto>("api/v1/admin/audit?" + string.Join("&", query), ct);
    }

    public Task<PasswordPolicyDto?> GetPasswordPolicyAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<PasswordPolicyDto?>(DesignPreviewData.PasswordPolicy)
            : GetAsync<PasswordPolicyDto>("api/v1/panel/security/policy", ct);

    public Task<PanelProfileDto?> GetPanelProfileAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<PanelProfileDto?>(DesignPreviewData.PanelProfile)
            : GetAsync<PanelProfileDto>("api/v1/panel/me", ct);

    public Task<BitrixIntegrationDto?> GetMyBitrixIntegrationAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<BitrixIntegrationDto?>(DesignPreviewData.MyBitrixIntegration)
            : GetAsync<BitrixIntegrationDto>("api/v1/panel/me/integrations/bitrix", ct);

    public Task<IReadOnlyList<OfficeBitrixWebhookDto>?> GetOfficeBitrixWebhooksAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<OfficeBitrixWebhookDto>?>([])
            : GetAsync<IReadOnlyList<OfficeBitrixWebhookDto>>("api/v1/panel/office/integrations/bitrix", ct);

    public Task<OfficeBitrixSettingsDto?> GetOfficeBitrixSettingsAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<OfficeBitrixSettingsDto?>(new OfficeBitrixSettingsDto(
                DesignPreviewData.PreviewOfficeId,
                "Основной",
                true))
            : GetAsync<OfficeBitrixSettingsDto>("api/v1/panel/office/bitrix-settings", ct);

    public async Task<(OfficeBitrixSettingsDto? Settings, string? Error)> UpdateOfficeBitrixSettingsAsync(
        bool transmissionEnabled,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (new OfficeBitrixSettingsDto(DesignPreviewData.PreviewOfficeId, "Основной", transmissionEnabled), null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, "api/v1/panel/office/bitrix-settings");
        request.Content = JsonContent.Create(new UpdateOfficeBitrixSettingsRequest(transmissionEnabled));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var settings = await response.Content.ReadFromJsonAsync<OfficeBitrixSettingsDto>(ct);
        return (settings, null);
    }

    public Task<ResponsesPageDto?> GetResponsesPageAsync(string query, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<ResponsesPageDto?>(new ResponsesPageDto([], 0, 1, 10))
            : GetAsync<ResponsesPageDto>($"api/v1/panel/responses?{query}", ct);

    public Task<ResponsesSummaryDto?> GetResponsesSummaryAsync(string query, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<ResponsesSummaryDto?>(new ResponsesSummaryDto(0, 0, 0, 0, null))
            : GetAsync<ResponsesSummaryDto>($"api/v1/panel/responses/summary?{query}", ct);

    public Task<IReadOnlyList<ResponseFilterAccountDto>?> GetResponseFilterAccountsAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<ResponseFilterAccountDto>?>([])
            : GetAsync<IReadOnlyList<ResponseFilterAccountDto>>("api/v1/panel/responses/filters/accounts", ct);

    public Task<ResponseDetailDto?> GetResponseDetailAsync(Guid id, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<ResponseDetailDto?>(null)
            : GetAsync<ResponseDetailDto>($"api/v1/panel/responses/{id}", ct);

    public async Task<ResendBitrixResultDto?> ResendResponseToBitrixAsync(Guid id, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return new ResendBitrixResultDto(true, ResponseStatuses.Sent, "1", null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/panel/responses/{id}/resend-bitrix");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<ResendBitrixResultDto>(ct);
    }

    public async Task<(BitrixIntegrationDto? Integration, string? Error)> SaveMyBitrixIntegrationAsync(
        string webhookUrl,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (DesignPreviewData.MyBitrixIntegration, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, "api/v1/panel/me/integrations/bitrix");
        request.Content = JsonContent.Create(new SaveBitrixIntegrationRequest(webhookUrl));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var integration = await response.Content.ReadFromJsonAsync<BitrixIntegrationDto>(ct);
        return integration is null ? (null, "Не удалось прочитать ответ API.") : (integration, null);
    }

    public async Task<(BitrixWebhookValidationDto? Validation, string? Error)> ValidateMyBitrixIntegrationAsync(
        string? webhookUrl,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (DesignPreviewData.BitrixValidationOk, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/panel/me/integrations/bitrix/validate");
        request.Content = JsonContent.Create(new ValidateBitrixIntegrationRequest(webhookUrl));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var validation = await response.Content.ReadFromJsonAsync<BitrixWebhookValidationDto>(ct);
        return validation is null ? (null, "Не удалось прочитать ответ API.") : (validation, null);
    }

    public Task<IReadOnlyList<BitrixIntegrationListItemDto>?> GetAdminBitrixIntegrationsAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<BitrixIntegrationListItemDto>?>(DesignPreviewData.BitrixIntegrations)
            : GetAsync<IReadOnlyList<BitrixIntegrationListItemDto>>("api/v1/admin/integrations/bitrix", ct);

    public Task<BitrixIntegrationDto?> GetAdminUserBitrixIntegrationAsync(string userId, CancellationToken ct = default) =>
        GetAsync<BitrixIntegrationDto>($"api/v1/admin/users/{userId}/integrations/bitrix", ct);

    public async Task<(BitrixIntegrationDto? Integration, string? Error)> SaveAdminUserBitrixIntegrationAsync(
        string userId,
        string webhookUrl,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/admin/users/{userId}/integrations/bitrix");
        request.Content = JsonContent.Create(new SaveBitrixIntegrationRequest(webhookUrl));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var integration = await response.Content.ReadFromJsonAsync<BitrixIntegrationDto>(ct);
        return integration is null ? (null, "Не удалось прочитать ответ API.") : (integration, null);
    }

    public async Task<(BitrixWebhookValidationDto? Validation, string? Error)> ValidateAdminUserBitrixIntegrationAsync(
        string userId,
        string? webhookUrl,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/admin/users/{userId}/integrations/bitrix/validate");
        request.Content = JsonContent.Create(new ValidateBitrixIntegrationRequest(webhookUrl));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var validation = await response.Content.ReadFromJsonAsync<BitrixWebhookValidationDto>(ct);
        return validation is null ? (null, "Не удалось прочитать ответ API.") : (validation, null);
    }

    public async Task<(bool Success, string? Error)> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (true, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/panel/me/password");
        request.Content = JsonContent.Create(new ChangeOwnPasswordRequest(currentPassword, newPassword));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public Task<WorkerReleaseListResponse?> GetWorkerReleasesAsync(CancellationToken ct = default) =>
        GetAsync<WorkerReleaseListResponse>("api/v1/admin/worker-releases", ct);

    public Task<WorkerReleaseLatestDto?> GetLatestWorkerReleaseAsync(CancellationToken ct = default) =>
        GetAsync<WorkerReleaseLatestDto>("api/v1/worker-releases/latest", ct);

    public async Task<(WorkerReleaseInfoDto? Release, string? Error)> UploadWorkerReleaseAsync(
        Stream packageStream,
        long fileLength,
        string fileName,
        string? version,
        string? releaseNotes,
        CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(packageStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        fileContent.Headers.ContentLength = fileLength;
        content.Add(fileContent, "packageFile", fileName);
        if (!string.IsNullOrWhiteSpace(version))
        {
            content.Add(new StringContent(version.Trim()), "version");
        }

        if (!string.IsNullOrWhiteSpace(releaseNotes))
        {
            content.Add(new StringContent(releaseNotes.Trim()), "releaseNotes");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/admin/worker-releases/upload");
        request.Content = content;
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var release = await response.Content.ReadFromJsonAsync<WorkerReleaseInfoDto>(ct);
        return release is null ? (null, "Не удалось прочитать ответ API.") : (release, null);
    }

    public async Task<(bool Success, string? Error)> SetWorkerReleaseLatestAsync(
        string version,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/admin/worker-releases/set-latest");
        request.Content = JsonContent.Create(new SetWorkerReleaseLatestRequest(version));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(LeadFlowImportPreviewDto? Preview, string? Error)> PreviewLeadFlowImportAsync(
        Stream databaseStream,
        long fileLength,
        string fileName,
        Guid officeId,
        string? encryptionKey,
        CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(databaseStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        fileContent.Headers.ContentLength = fileLength;
        content.Add(fileContent, "databaseFile", fileName);
        content.Add(new StringContent(officeId.ToString()), "officeId");
        if (!string.IsNullOrWhiteSpace(encryptionKey))
        {
            content.Add(new StringContent(encryptionKey.Trim()), "encryptionKey");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/admin/leadflow-import/preview");
        request.Content = content;
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var preview = await response.Content.ReadFromJsonAsync<LeadFlowImportPreviewDto>(ct);
        return preview is null ? (null, "Не удалось прочитать ответ API.") : (preview, null);
    }

    public async Task<(LeadFlowImportExecuteResultDto? Result, string? Error)> ExecuteLeadFlowImportAsync(
        Guid sessionId,
        IReadOnlyList<Guid>? selectedIds,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/admin/leadflow-import/execute");
        request.Content = JsonContent.Create(new LeadFlowImportExecuteRequest(sessionId, selectedIds));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var result = await response.Content.ReadFromJsonAsync<LeadFlowImportExecuteResultDto>(ct);
        return result is null ? (null, "Не удалось прочитать ответ API.") : (result, null);
    }

    public async Task<(bool Success, string? Error)> DeleteWorkerReleaseAsync(
        string version,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/admin/worker-releases/delete");
        request.Content = JsonContent.Create(new DeleteWorkerReleaseRequest(version));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(Stream? Stream, string? FileName, string? Error)> OpenLatestWorkerReleaseDownloadAsync(
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/public/worker-releases/latest/download");
        using var response = await SendAsyncSafe(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            return (null, null, await ReadApiErrorAsync(response, ct));
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? "Orbita.Worker.Setup.msi";
        return (stream, fileName, null);
    }

    public async Task<(Stream? Stream, string? FileName, string? Error)> OpenWorkerReleaseDownloadAsync(
        string version,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/admin/worker-releases/{Uri.EscapeDataString(version)}/download");
        using var response = await SendAuthenticatedAsync(request, ct, HttpCompletionOption.ResponseHeadersRead);
        if (response is null)
        {
            return (null, null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, null, await ReadApiErrorAsync(response, ct));
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? $"Orbita.Worker.Setup-{version}.msi";
        return (stream, fileName, null);
    }

    private async Task<(bool Success, string? Error)> PostAdminActionAsync(string url, CancellationToken ct) =>
        await PostPanelActionAsync(url, ct);

    private async Task<(bool Success, string? Error)> PostPanelActionAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    private static async Task<string?> ReadApiErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(ct);
            if (!string.IsNullOrWhiteSpace(payload?.Error))
            {
                return payload.Error;
            }
        }
        catch
        {
            // ignore parse errors
        }

        return "Не удалось выполнить операцию.";
    }

    private sealed record ApiErrorResponse(string? Error);
}

public sealed record LoginResponse(string Token, string Email);
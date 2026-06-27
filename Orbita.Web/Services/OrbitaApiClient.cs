using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class OrbitaApiClient(HttpClient http, AuthSession session, IOptions<DesignPreviewOptions> previewOptions)
{
    private readonly DesignPreviewOptions _preview = previewOptions.Value;

    public Task<LoginResponse?> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            if (string.Equals(email, _preview.Email, StringComparison.OrdinalIgnoreCase)
                && password == _preview.Password)
            {
                return Task.FromResult<LoginResponse?>(new LoginResponse("design-preview", email));
            }

            return Task.FromResult<LoginResponse?>(null);
        }

        return LoginViaApiAsync(email, password, ct);
    }

    private async Task<LoginResponse?> LoginViaApiAsync(string email, string password, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("api/v1/auth/login", new { email, password }, ct);
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

    public Task<IReadOnlyList<WorkerEventListItem>?> GetEventsAsync(Guid? workerId = null, int limit = 100, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            var events = DesignPreviewData.Events;
            if (workerId.HasValue)
            {
                events = events.Where(e => e.WorkerId == workerId.Value).ToList();
            }

            return Task.FromResult<IReadOnlyList<WorkerEventListItem>?>(events.Take(limit).ToList());
        }

        var url = workerId.HasValue
            ? $"api/v1/events?workerId={workerId}&limit={limit}"
            : $"api/v1/events?limit={limit}";
        return GetAsync<IReadOnlyList<WorkerEventListItem>>(url, ct);
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request);
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(ct);
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

    public async Task<(bool Success, string? Error)> CreatePanelUserAsync(
        string email,
        string password,
        string role,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/admin/users");
        ApplyAuth(request);
        request.Content = JsonContent.Create(new CreatePanelUserRequest(email, password, role));
        var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        return (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> DeletePanelUserAsync(string userId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/admin/users/{userId}");
        ApplyAuth(request);
        var response = await http.SendAsync(request, ct);
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
        ApplyAuth(request);
        request.Content = JsonContent.Create(new ResetPanelUserPasswordRequest(password));
        var response = await http.SendAsync(request, ct);
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
        ApplyAuth(request);
        request.Content = JsonContent.Create(new UpdatePanelUserRoleRequest(role));
        var response = await http.SendAsync(request, ct);
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
        ApplyAuth(request);
        request.Content = JsonContent.Create(new UpdateAdminWorkerRequest(displayName));
        var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> SetAdminWorkerEnabledAsync(
        Guid workerId,
        bool enabled,
        CancellationToken ct = default) =>
        await PostAdminActionAsync($"api/v1/admin/workers/{workerId}/{(enabled ? "enable" : "disable")}", ct);

    public async Task<(RotateWorkerApiKeyResponse? Result, string? Error)> RotateWorkerApiKeyAsync(
        Guid workerId,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/admin/workers/{workerId}/rotate-key");
        ApplyAuth(request);
        var response = await http.SendAsync(request, ct);
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
        ApplyAuth(request);
        request.Content = JsonContent.Create(new ChangeOwnPasswordRequest(currentPassword, newPassword));
        var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    private async Task<(bool Success, string? Error)> PostAdminActionAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyAuth(request);
        var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(session.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        }
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
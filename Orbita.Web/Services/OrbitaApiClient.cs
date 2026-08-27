using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class OrbitaApiClient(
    HttpClient http,
    AuthSession session,
    IOfficeContext officeContext,
    IOptions<DesignPreviewOptions> previewOptions)
{
    private const string InvalidApiSessionError =
        "Сессия недействительна. Выйдите из панели и войдите снова.";

    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly DesignPreviewOptions _preview = previewOptions.Value;

    private bool CanUseAuthenticatedApi =>
        !_preview.Enabled && IsJwtToken(session.Token);

    private static bool IsJwtToken(string? token) =>
        !string.IsNullOrWhiteSpace(token)
        && !string.Equals(token, AuthSession.DesignPreviewToken, StringComparison.Ordinal)
        && token.Count(c => c == '.') >= 2;

    public Task<LoginResponse?> LoginAsync(string email, string password, CancellationToken ct = default) =>
        LoginAsync(email, password, rememberMe: false, ct);

    public Task<LoginResponse?> LoginAsync(string email, string password, bool rememberMe, CancellationToken ct = default)
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

        return LoginViaApiAsync(email, password, rememberMe, ct);
    }

    private async Task<LoginResponse?> LoginViaApiAsync(string email, string password, bool rememberMe, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email, password, rememberMe })
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

    public Task<GlobalDashboardSummary?> GetSummaryAsync(
        int timeZoneOffsetMinutes = 0,
        DateTime? fromLocal = null,
        DateTime? toLocal = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return Task.FromResult<GlobalDashboardSummary?>(DesignPreviewData.GetSummary(officeContext.EffectiveOfficeId));
        }

        var path = WithOfficeQuery("api/v1/dashboard/summary");
        path = AppendQuery(path, "tz", timeZoneOffsetMinutes.ToString());
        if (fromLocal is DateTime from)
        {
            path = AppendQuery(path, "from", from.ToString("yyyy-MM-dd"));
        }

        if (toLocal is DateTime to)
        {
            path = AppendQuery(path, "to", to.ToString("yyyy-MM-dd"));
        }

        return GetAsync<GlobalDashboardSummary>(path, ct);
    }

    public Task<NavBadgesDto?> GetNavBadgesAsync(int timeZoneOffsetMinutes = 0, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            var summary = DesignPreviewData.GetSummary(officeContext.EffectiveOfficeId);
            return Task.FromResult<NavBadgesDto?>(new NavBadgesDto(
                summary.Errors,
                summary.UniqueResponsesToday,
                summary.ActionRequired,
                DateTime.UtcNow));
        }

        var path = WithOfficeQuery("api/v1/nav/badges");
        path = AppendQuery(path, "tz", timeZoneOffsetMinutes.ToString());
        return GetAsync<NavBadgesDto>(path, ct);
    }

    public Task<IReadOnlyList<WorkerListItem>?> GetWorkersAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<WorkerListItem>?>(DesignPreviewData.GetWorkers(officeContext.EffectiveOfficeId))
            : GetAsync<IReadOnlyList<WorkerListItem>>(WithOfficeQuery("api/v1/workers"), ct);

    public Task<WorkerDetail?> GetWorkerAsync(Guid id, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult(DesignPreviewData.GetWorker(id))
            : GetAsync<WorkerDetail>($"api/v1/workers/{id}", ct);

    public Task<IReadOnlyList<WorkerAccountDto>?> GetWorkerAccountsAsync(Guid id, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<WorkerAccountDto>?>(DesignPreviewData.GetAccounts(id))
            : GetAsync<IReadOnlyList<WorkerAccountDto>>($"api/v1/workers/{id}/accounts", ct);

    public Task<IReadOnlyList<OfficeAccountListItem>?> GetOfficeAccountsAsync(
        Guid? workerId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return Task.FromResult<IReadOnlyList<OfficeAccountListItem>?>(
                DesignPreviewData.GetOfficeAccounts(officeContext.EffectiveOfficeId, workerId));
        }

        var path = WithOfficeQuery("api/v1/accounts");
        if (workerId is Guid id)
        {
            path = AppendQuery(path, "workerId", id.ToString("D"));
        }

        return GetAsync<IReadOnlyList<OfficeAccountListItem>>(path, ct);
    }

    public Task<IReadOnlyList<WorkerEventListItem>?> GetEventsAsync(
        Guid? workerId = null,
        int limit = 100,
        DateTime? sinceUtc = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            var events = DesignPreviewData.GetEvents(officeContext.EffectiveOfficeId);
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

        AppendOfficeQuery(parts);
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

    private string WithOfficeQuery(string url, Guid? officeId = null)
    {
        var resolvedOfficeId = officeId ?? officeContext.EffectiveOfficeId;
        if (resolvedOfficeId is not Guid effectiveOfficeId)
        {
            return url;
        }

        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}officeId={effectiveOfficeId:D}";
    }

    private void AppendOfficeQuery(List<string> parts, Guid? officeId = null)
    {
        var resolvedOfficeId = officeId ?? officeContext.EffectiveOfficeId;
        if (resolvedOfficeId is Guid effectiveOfficeId)
        {
            parts.Add($"officeId={effectiveOfficeId:D}");
        }
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(ApiJsonOptions, ct);
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

    public Task<PanelUserPresenceHourSeriesDto?> GetPanelUserPresenceHourSeriesAsync(CancellationToken ct = default) =>
        GetAsync<PanelUserPresenceHourSeriesDto>("api/v1/admin/users/presence-stats", ct);

    public async Task RecordActivityAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/activity");
        using var response = await SendAuthenticatedAsync(request, ct);
    }

    public Task<IReadOnlyList<PanelUserDto>?> GetOfficeStaffUsersAsync(
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return Task.FromResult<IReadOnlyList<PanelUserDto>?>(DesignPreviewData.GetOfficeStaffUsers(officeId));
        }

        return GetAsync<IReadOnlyList<PanelUserDto>>(WithOfficeQuery("api/v1/office-staff/users", officeId), ct);
    }

    public async Task<(bool Success, string? Error)> CreateOfficeStaffUserAsync(
        string email,
        string fullName,
        string password,
        string role,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.CreateOfficeStaffUser(email, fullName, password, role, officeId);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, WithOfficeQuery("api/v1/office-staff/users", officeId));
        request.Content = JsonContent.Create(new CreatePanelUserRequest(email, password, role, officeId, fullName));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> UpdateOfficeStaffFullNameAsync(
        string userId,
        string fullName,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.UpdateOfficeStaffFullName(userId, fullName);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            WithOfficeQuery($"api/v1/office-staff/users/{Uri.EscapeDataString(userId)}/full-name", officeId));
        request.Content = JsonContent.Create(new UpdatePanelUserFullNameRequest(fullName));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> UpdateOfficeStaffRoleAsync(
        string userId,
        string role,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.UpdateOfficeStaffRole(userId, role);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            WithOfficeQuery($"api/v1/office-staff/users/{Uri.EscapeDataString(userId)}/role", officeId));
        request.Content = JsonContent.Create(new UpdatePanelUserRoleRequest(role));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> ResetOfficeStaffPasswordAsync(
        string userId,
        string password,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (true, null);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            WithOfficeQuery($"api/v1/office-staff/users/{Uri.EscapeDataString(userId)}/password", officeId));
        request.Content = JsonContent.Create(new ResetPanelUserPasswordRequest(password));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> LockOfficeStaffUserAsync(
        string userId,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.SetOfficeStaffLocked(userId, locked: true);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            WithOfficeQuery($"api/v1/office-staff/users/{Uri.EscapeDataString(userId)}/lock", officeId));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> UnlockOfficeStaffUserAsync(
        string userId,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.SetOfficeStaffLocked(userId, locked: false);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            WithOfficeQuery($"api/v1/office-staff/users/{Uri.EscapeDataString(userId)}/unlock", officeId));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> DeleteOfficeStaffUserAsync(
        string userId,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.DeleteOfficeStaffUser(userId);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            WithOfficeQuery($"api/v1/office-staff/users/{Uri.EscapeDataString(userId)}", officeId));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public Task<ServiceLogsPageDto?> GetServiceLogsAsync(
        string? q,
        string? level,
        string? service,
        DateTime? date,
        Guid? workerId,
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

        if (workerId.HasValue)
        {
            query.Add($"workerId={workerId.Value:D}");
        }

        query.Add($"page={page}");
        query.Add($"pageSize={pageSize}");
        var url = "api/v1/admin/logs?" + string.Join("&", query);
        return GetAsync<ServiceLogsPageDto>(url, ct);
    }

    public Task<IReadOnlyList<OfficeDto>?> GetOfficesAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<OfficeDto>?>(DesignPreviewData.Offices)
            : GetAsync<IReadOnlyList<OfficeDto>>("api/v1/admin/offices", ct);

    public Task<IReadOnlyList<OfficeOptionDto>?> GetOfficeOptionsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<OfficeOptionDto>>("api/v1/panel/offices/options", ct);

    public Task<OfficeDetailDto?> GetOfficeAsync(Guid id, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult(DesignPreviewData.GetOfficeDetail(id))
            : GetAsync<OfficeDetailDto>($"api/v1/admin/offices/{id}", ct);

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
        bool crmEnabled,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/admin/offices/{id}");
        request.Content = JsonContent.Create(new UpdateOfficeRequest(name, isEnabled, bitrixTransmissionEnabled, crmEnabled));
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

    public async Task<(bool Success, string? Error)> DeleteOfficeAsync(Guid id, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/admin/offices/{id}");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> CreatePanelUserAsync(
        string email,
        string fullName,
        string password,
        string role,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/admin/users");
        request.Content = JsonContent.Create(new CreatePanelUserRequest(email, password, role, officeId, fullName));
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

    public Task<IReadOnlyList<AccessProfileDto>?> GetAccessProfilesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AccessProfileDto>>("api/v1/admin/access-profiles", ct);

    public async Task<(bool Success, string? Error)> UpdateAccessProfileAsync(
        string profileId,
        IReadOnlyList<string> permissions,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/admin/access-profiles/{profileId}");
        request.Content = JsonContent.Create(new UpdateAccessProfileRequest(permissions));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> UpdatePanelUserFullNameAsync(
        string userId,
        string fullName,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/admin/users/{userId}/full-name");
        request.Content = JsonContent.Create(new UpdatePanelUserFullNameRequest(fullName));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
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

    public async Task<(bool Success, string? Error)> UpdatePanelUserPermissionsAsync(
        string userId,
        bool useProfilePermissions,
        IReadOnlyList<string> permissions,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/admin/users/{userId}/permissions");
        request.Content = JsonContent.Create(new UpdatePanelUserPermissionsRequest(useProfilePermissions, permissions));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
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

    public async Task<(BulkWorkersMonitoringResultDto? Result, string? Error)> SetAllWorkersEnabledAsync(
        bool enabled,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/panel/workers/{(enabled ? "enable-all" : "disable-all")}");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            var apiError = await ReadApiErrorAsync(response, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (null, "bulk-endpoint-missing");
            }

            return (null, apiError);
        }

        var result = await response.Content.ReadFromJsonAsync<BulkWorkersMonitoringResultDto>(ApiJsonOptions, ct);
        return result is null
            ? (null, "Не удалось прочитать ответ API.")
            : (result, null);
    }

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
        string? adsPowerGroupId = null,
        bool responseFilterEnabled = false,
        bool responseFilterExcludeFemale = false,
        bool responseFilterExcludeMale = false,
        int? responseFilterMaxAgeMale = null,
        int? responseFilterMaxAgeFemale = null,
        int? responseFilterMaxAgeDays = null,
        bool responseHighlightEnabled = false,
        string? responseHighlightAgeBuckets = null,
        bool autoScheduleEnabled = false,
        string? autoScheduleDays = null,
        string? autoScheduleFromLocalTime = null,
        string? autoScheduleToLocalTime = null,
        bool messengerAutoReplyEnabled = false,
        string? messengerAutoReplyMessage = null,
        int? phoneUnchangedHours = null,
        bool? autoDeliverToCrm = null,
        bool? autoDeliverToBitrix = null,
        string? responseHighlightTargetsJson = null,
        string? ruCaptchaApiKey = null,
        string? multiloginLauncherUrl = null,
        string? multiloginCloudApiUrl = null,
        string? multiloginAutomationToken = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/workers/{workerId}/settings");
        request.Content = JsonContent.Create(new UpdateWorkerSettingsRequest(
            maxConcurrentAccounts,
            adsPowerApiBaseUrl,
            adsPowerApiKey,
            responseFilterEnabled,
            responseFilterExcludeFemale,
            ResponseFilterMaxAge: null,
            responseFilterExcludeMale,
            responseFilterMaxAgeMale,
            responseFilterMaxAgeFemale,
            responseFilterMaxAgeDays,
            responseHighlightEnabled,
            responseHighlightAgeBuckets,
            autoScheduleEnabled,
            autoScheduleDays,
            autoScheduleFromLocalTime,
            autoScheduleToLocalTime,
            messengerAutoReplyEnabled,
            messengerAutoReplyMessage,
            phoneUnchangedHours,
            autoDeliverToCrm,
            autoDeliverToBitrix,
            responseHighlightTargetsJson,
            adsPowerGroupId,
            ruCaptchaApiKey,
            multiloginLauncherUrl,
            multiloginCloudApiUrl,
            multiloginAutomationToken));
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

    public async Task<(bool Success, string? Error)> UpdateWorkerAccountCredentialsAsync(
        Guid workerId,
        Guid accountId,
        string? login,
        string? password,
        bool clear,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"api/v1/workers/{workerId}/accounts/{accountId}/credentials");
        request.Content = JsonContent.Create(new UpdateWorkerAccountCredentialsRequest(login, password, clear));
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

    public Task<PasswordPolicyDto?> GetPasswordPolicyAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<PasswordPolicyDto?>(DesignPreviewData.PasswordPolicy)
            : GetAsync<PasswordPolicyDto>("api/v1/panel/security/policy", ct);

    public Task<PanelProfileDto?> GetPanelProfileAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<PanelProfileDto?>(DesignPreviewData.PanelProfile)
            : GetAsync<PanelProfileDto>("api/v1/panel/me", ct);

    public Task<OfficeBitrixIntegrationDto?> GetOfficeBitrixIntegrationAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<OfficeBitrixIntegrationDto?>(DesignPreviewData.OfficeBitrixIntegration)
            : GetAsync<OfficeBitrixIntegrationDto>("api/v1/panel/office/integrations/bitrix", ct);

    public Task<OfficeBitrixSettingsDto?> GetOfficeBitrixSettingsAsync(
        Guid? officeId = null,
        CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<OfficeBitrixSettingsDto?>(new OfficeBitrixSettingsDto(
                DesignPreviewData.PreviewOfficeId,
                "Основной",
                true))
            : GetAsync<OfficeBitrixSettingsDto>(
                WithOfficeQuery("api/v1/panel/office/bitrix-settings", officeId),
                ct);

    public async Task<(OfficeBitrixSettingsDto? Settings, string? Error)> UpdateOfficeBitrixSettingsAsync(
        bool transmissionEnabled,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (new OfficeBitrixSettingsDto(DesignPreviewData.PreviewOfficeId, "Основной", transmissionEnabled), null);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            WithOfficeQuery("api/v1/panel/office/bitrix-settings", officeId));
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
            ? Task.FromResult<ResponsesPageDto?>(DesignPreviewData.GetResponsesPage(query, officeContext.EffectiveOfficeId))
            : GetAsync<ResponsesPageDto>(WithOfficeQuery($"api/v1/panel/responses?{query}"), ct);

    public Task<ResponsesSummaryDto?> GetResponsesSummaryAsync(string query, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<ResponsesSummaryDto?>(DesignPreviewData.GetResponsesSummary(officeContext.EffectiveOfficeId))
            : GetAsync<ResponsesSummaryDto>(WithOfficeQuery($"api/v1/panel/responses/summary?{query}"), ct);

    public Task<OfficeStatisticsDto?> GetStatisticsAsync(
        DateTime from,
        DateTime to,
        IReadOnlyList<Guid>? workerIds = null,
        IReadOnlyList<Guid>? accountIds = null,
        string? vacancy = null,
        int timeZoneOffsetMinutes = 0,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return Task.FromResult<OfficeStatisticsDto?>(
                DesignPreviewData.GetStatistics(officeContext.EffectiveOfficeId, from, to, workerIds, accountIds));
        }

        var query = BuildStatisticsQuery(from, to, workerIds, accountIds, vacancy, timeZoneOffsetMinutes);
        return GetAsync<OfficeStatisticsDto>(WithOfficeQuery($"api/v1/panel/statistics?{query}"), ct);
    }

    internal static string BuildStatisticsQuery(
        DateTime from,
        DateTime to,
        IReadOnlyList<Guid>? workerIds,
        IReadOnlyList<Guid>? accountIds,
        string? vacancy = null,
        int timeZoneOffsetMinutes = 0)
    {
        var parts = new List<string>
        {
            $"from={from:yyyy-MM-dd}",
            $"to={to:yyyy-MM-dd}",
            $"tz={timeZoneOffsetMinutes}"
        };

        if (workerIds is not null)
        {
            parts.AddRange(workerIds.Select(id => $"workerIds={id}"));
        }

        if (accountIds is not null)
        {
            parts.AddRange(accountIds.Select(id => $"accountIds={id}"));
        }

        if (!string.IsNullOrWhiteSpace(vacancy))
        {
            parts.Add($"vacancy={Uri.EscapeDataString(vacancy)}");
        }

        return string.Join('&', parts);
    }

    public Task<IReadOnlyList<ResponseFilterVacancyDto>?> GetResponseFilterVacanciesAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<ResponseFilterVacancyDto>?>([])
            : GetAsync<IReadOnlyList<ResponseFilterVacancyDto>>(
                WithOfficeQuery(
                    $"api/v1/panel/responses/filters/vacancies?from={Uri.EscapeDataString(fromUtc.ToString("o"))}&to={Uri.EscapeDataString(toUtc.ToString("o"))}"),
                ct);

    public Task<IReadOnlyList<ResponseFilterAccountDto>?> GetResponseFilterAccountsAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<ResponseFilterAccountDto>?>([])
            : GetAsync<IReadOnlyList<ResponseFilterAccountDto>>(
                WithOfficeQuery("api/v1/panel/responses/filters/accounts"),
                ct);

    public Task<ResponseDetailDto?> GetResponseDetailAsync(Guid id, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<ResponseDetailDto?>(null)
            : GetAsync<ResponseDetailDto>($"api/v1/panel/responses/{id}", ct);

    public async Task<(UpdateResponseResultDto? Result, string? Error)> UpdateResponseAsync(
        Guid id,
        UpdateResponseRequest body,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (new UpdateResponseResultDto(true, null), null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/panel/responses/{id:D}");
        request.Content = JsonContent.Create(body);
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var result = await response.Content.ReadFromJsonAsync<UpdateResponseResultDto>(ApiJsonOptions, ct);
        return result is null
            ? (null, "Не удалось прочитать ответ API.")
            : (result, null);
    }

    public async Task<(Stream? Stream, string? ContentType)> GetResponseAvatarAsync(Guid id, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (null, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/panel/responses/{id}/avatar");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            return (null, null);
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var buffer = await response.Content.ReadAsByteArrayAsync(ct);
        return buffer.Length == 0 ? (null, contentType) : (new MemoryStream(buffer), contentType);
    }

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

    public Task<IReadOnlyList<BitrixInstanceListItemDto>?> GetBitrixInstancesAsync(
        Guid? officeId = null,
        CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<BitrixInstanceListItemDto>?>(DesignPreviewData.PreviewBitrixInstances)
            : GetAsync<IReadOnlyList<BitrixInstanceListItemDto>>(
                WithOfficeQuery("api/v1/panel/bitrix-instances", officeId),
                ct);

    public Task<BitrixInstanceDto?> GetBitrixInstanceAsync(
        Guid id,
        Guid? officeId = null,
        CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult(DesignPreviewData.GetPreviewBitrixInstance(id))
            : GetAsync<BitrixInstanceDto>(WithOfficeQuery($"api/v1/panel/bitrix-instances/{id:D}", officeId), ct);

    public async Task<(BitrixCrmImportPreviewDto? Preview, string? Error)> PreviewBitrixCrmImportAsync(
        Guid id,
        BitrixCrmImportPreviewRequest importRequest,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (DesignPreviewData.PreviewBitrixCrmImport, null);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            WithOfficeQuery($"api/v1/panel/bitrix-instances/{id:D}/crm-import/preview", officeId));
        request.Content = JsonContent.Create(importRequest);
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var preview = await response.Content.ReadFromJsonAsync<BitrixCrmImportPreviewDto>(ApiJsonOptions, ct);
        return preview is null ? (null, "Не удалось прочитать предпросмотр импорта.") : (preview, null);
    }

    public async Task<(BitrixCrmImportResultDto? Result, string? Error)> ExecuteBitrixCrmImportAsync(
        Guid id,
        BitrixCrmImportExecuteRequest importRequest,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            var selected = importRequest.DealIds?.Count ?? DesignPreviewData.PreviewBitrixCrmImport.Deals.Count;
            return (new BitrixCrmImportResultDto(selected, selected, 0, 0, 0, []), null);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            WithOfficeQuery($"api/v1/panel/bitrix-instances/{id:D}/crm-import", officeId));
        request.Content = JsonContent.Create(importRequest);
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var result = await response.Content.ReadFromJsonAsync<BitrixCrmImportResultDto>(ApiJsonOptions, ct);
        return result is null ? (null, "Не удалось прочитать результат импорта.") : (result, null);
    }

    public Task<BitrixWorkforceSettingsDto?> GetBitrixWorkforceSettingsAsync(
        Guid id,
        Guid? officeId = null,
        CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<BitrixWorkforceSettingsDto?>(DesignPreviewData.PreviewBitrixWorkforceSettings)
            : GetAsync<BitrixWorkforceSettingsDto>(
                WithOfficeQuery($"api/v1/panel/bitrix-instances/{id:D}/workforce", officeId),
                ct);

    public Task<IReadOnlyList<BitrixWorkforceAssignmentDto>?> GetBitrixWorkforceAssignmentsAsync(
        Guid id,
        Guid? officeId = null,
        int take = 50,
        CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<BitrixWorkforceAssignmentDto>?>(DesignPreviewData.PreviewBitrixWorkforceAssignments)
            : GetAsync<IReadOnlyList<BitrixWorkforceAssignmentDto>>(
                WithOfficeQuery(
                    $"api/v1/panel/bitrix-instances/{id:D}/workforce/assignments?take={Math.Clamp(take, 1, 200)}",
                    officeId),
                ct);

    public async Task<(BitrixWorkforceSettingsDto? Settings, string? Error)> UpdateBitrixWorkforceSettingsAsync(
        Guid id,
        UpdateBitrixWorkforceSettingsRequest settings,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (DesignPreviewData.PreviewBitrixWorkforceSettings, null);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            WithOfficeQuery($"api/v1/panel/bitrix-instances/{id:D}/workforce", officeId));
        request.Content = JsonContent.Create(settings);
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var saved = await response.Content.ReadFromJsonAsync<BitrixWorkforceSettingsDto>(ApiJsonOptions, ct);
        return saved is null ? (null, "Не удалось прочитать ответ API.") : (saved, null);
    }

    public async Task<(BitrixWorkforceReceiverDto? Receiver, string? Error)> ConfigureBitrixWorkforceReceiverAsync(
        Guid id,
        ConfigureBitrixWorkforceReceiverRequest receiver,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (
                new BitrixWorkforceReceiverDto(
                    true,
                    DesignPreviewData.PreviewBitrixWorkforceSettings.EventEndpointUrl,
                    receiver.ExpectedMemberId,
                    DateTime.UtcNow),
                null);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            WithOfficeQuery($"api/v1/panel/bitrix-instances/{id:D}/workforce/receiver", officeId));
        request.Content = JsonContent.Create(receiver);
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var saved = await response.Content.ReadFromJsonAsync<BitrixWorkforceReceiverDto>(ApiJsonOptions, ct);
        return saved is null ? (null, "Не удалось прочитать ответ API.") : (saved, null);
    }

    public async Task<(BitrixInstanceDto? Instance, string? Error)> CreateBitrixInstanceAsync(
        CreateBitrixInstanceRequest request,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (DesignPreviewData.GetPreviewBitrixInstance(Guid.Empty), null);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, WithOfficeQuery("api/v1/panel/bitrix-instances", officeId));
        httpRequest.Content = JsonContent.Create(request);
        using var response = await SendAuthenticatedAsync(httpRequest, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var instance = await response.Content.ReadFromJsonAsync<BitrixInstanceDto>(ApiJsonOptions, ct);
        return instance is null ? (null, "Не удалось прочитать ответ API.") : (instance, null);
    }

    public async Task<(BitrixInstanceDto? Instance, string? Error)> UpdateBitrixInstanceAsync(
        Guid id,
        UpdateBitrixInstanceRequest request,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (DesignPreviewData.GetPreviewBitrixInstance(id), null);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, WithOfficeQuery($"api/v1/panel/bitrix-instances/{id:D}", officeId));
        httpRequest.Content = JsonContent.Create(request);
        using var response = await SendAuthenticatedAsync(httpRequest, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var instance = await response.Content.ReadFromJsonAsync<BitrixInstanceDto>(ApiJsonOptions, ct);
        return instance is null ? (null, "Не удалось прочитать ответ API.") : (instance, null);
    }

    public async Task<(bool Success, string? Error)> DeleteBitrixInstanceAsync(
        Guid id,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (true, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, WithOfficeQuery($"api/v1/panel/bitrix-instances/{id:D}", officeId));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(BitrixWebhookValidationDto? Validation, string? Error)> ValidateBitrixWebhookAsync(
        string? webhookUrl,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (DesignPreviewData.BitrixValidationOk, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, WithOfficeQuery("api/v1/panel/bitrix-instances/validate-webhook", officeId));
        request.Content = JsonContent.Create(new ValidateBitrixInstanceRequest(webhookUrl));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var validation = await response.Content.ReadFromJsonAsync<BitrixWebhookValidationDto>(ApiJsonOptions, ct);
        return validation is null ? (null, "Не удалось прочитать ответ API.") : (validation, null);
    }

    public async Task<(BitrixWebhookValidationDto? Validation, string? Error)> ValidateBitrixInstanceAsync(
        Guid id,
        string? webhookUrl,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (DesignPreviewData.BitrixValidationOk, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, WithOfficeQuery($"api/v1/panel/bitrix-instances/{id:D}/validate", officeId));
        request.Content = JsonContent.Create(new ValidateBitrixInstanceRequest(webhookUrl));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var validation = await response.Content.ReadFromJsonAsync<BitrixWebhookValidationDto>(ApiJsonOptions, ct);
        return validation is null ? (null, "Не удалось прочитать ответ API.") : (validation, null);
    }

    public Task<DistributionRouteDto?> GetDistributionRouteAsync(
        Guid? officeId = null,
        CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<DistributionRouteDto?>(DesignPreviewData.PreviewDistributionRoute)
            : GetAsync<DistributionRouteDto>(WithOfficeQuery("api/v1/panel/distribution-route", officeId), ct);

    public async Task<(DistributionRouteDto? Route, string? Error)> SaveDistributionRouteAsync(
        SaveDistributionRouteRequest request,
        Guid? officeId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (DesignPreviewData.PreviewDistributionRoute, null);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, WithOfficeQuery("api/v1/panel/distribution-route", officeId));
        httpRequest.Content = JsonContent.Create(request);
        using var response = await SendAuthenticatedAsync(httpRequest, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var route = await response.Content.ReadFromJsonAsync<DistributionRouteDto>(ApiJsonOptions, ct);
        return route is null ? (null, "Не удалось прочитать ответ API.") : (route, null);
    }

    public async Task<SendBitrixResultDto?> SendResponseToBitrixAsync(
        Guid id,
        Guid bitrixInstanceId,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return new SendBitrixResultDto(true, ResponseStatuses.Sent, "1", bitrixInstanceId, "Основной", null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/panel/responses/{id:D}/send-bitrix");
        request.Content = JsonContent.Create(new SendResponseToBitrixRequest(bitrixInstanceId));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<SendBitrixResultDto>(ApiJsonOptions, ct);
    }

    public async Task<DeliverResponseResultDto?> DeliverResponseAsync(
        Guid id,
        DeliverResponseRequest body,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return new DeliverResponseResultDto(
                true,
                ResponseStatuses.Sent,
                null,
                body.OfficeId,
                [new DeliverResponseChannelResultDto("CRM", true, "Sent", null)]);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/panel/responses/{id:D}/deliver");
        request.Content = JsonContent.Create(body);
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DeliverResponseResultDto>(ApiJsonOptions, ct);
    }

    public async Task<(BulkDeliverResponsesResultDto? Result, string? Error)> DeliverResponsesBulkAsync(
        BulkDeliverResponsesRequest body,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            var items = body.ResponseIds
                .Select(id => new BulkDeliverItemResultDto(id, true, ResponseStatuses.Sent, null))
                .ToList();
            return (new BulkDeliverResponsesResultDto(body.ResponseIds.Count, body.ResponseIds.Count, 0, items), null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/panel/responses/deliver-bulk");
        request.Content = JsonContent.Create(body);
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var result = await response.Content.ReadFromJsonAsync<BulkDeliverResponsesResultDto>(ApiJsonOptions, ct);
        return result is null ? (null, "Не удалось прочитать ответ API.") : (result, null);
    }

    public async Task<(BulkSendBitrixResultDto? Result, string? Error)> BulkSendResponsesToBitrixAsync(
        IReadOnlyList<Guid> responseIds,
        Guid bitrixInstanceId,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            var items = responseIds
                .Select(id => new BulkSendBitrixItemResultDto(id, true, ResponseStatuses.Sent, null))
                .ToList();
            return (new BulkSendBitrixResultDto(responseIds.Count, responseIds.Count, 0, items), null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/panel/responses/send-bitrix-bulk");
        request.Content = JsonContent.Create(new BulkSendResponsesToBitrixRequest(responseIds, bitrixInstanceId));
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var result = await response.Content.ReadFromJsonAsync<BulkSendBitrixResultDto>(ApiJsonOptions, ct);
        return result is null ? (null, "Не удалось прочитать ответ API.") : (result, null);
    }

    public async Task<(OfficeBitrixIntegrationDto? Integration, string? Error)> SaveOfficeBitrixIntegrationAsync(
        string webhookUrl,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (DesignPreviewData.OfficeBitrixIntegration, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, "api/v1/panel/office/integrations/bitrix");
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

        var integration = await response.Content.ReadFromJsonAsync<OfficeBitrixIntegrationDto>(ct);
        return integration is null ? (null, "Не удалось прочитать ответ API.") : (integration, null);
    }

    public async Task<(BitrixWebhookValidationDto? Validation, string? Error)> ValidateOfficeBitrixIntegrationAsync(
        string? webhookUrl,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (DesignPreviewData.BitrixValidationOk, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/panel/office/integrations/bitrix/validate");
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

    public Task<IReadOnlyList<OfficeDto>?> GetAdminBitrixIntegrationsAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<OfficeDto>?>(DesignPreviewData.Offices)
            : GetAsync<IReadOnlyList<OfficeDto>>("api/v1/admin/integrations/bitrix", ct);

    public Task<OfficeBitrixIntegrationDto?> GetAdminOfficeBitrixIntegrationAsync(Guid officeId, CancellationToken ct = default) =>
        GetAsync<OfficeBitrixIntegrationDto>($"api/v1/admin/offices/{officeId:D}/integrations/bitrix", ct);

    public async Task<(OfficeBitrixIntegrationDto? Integration, string? Error)> SaveAdminOfficeBitrixIntegrationAsync(
        Guid officeId,
        string webhookUrl,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/admin/offices/{officeId:D}/integrations/bitrix");
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

        var integration = await response.Content.ReadFromJsonAsync<OfficeBitrixIntegrationDto>(ct);
        return integration is null ? (null, "Не удалось прочитать ответ API.") : (integration, null);
    }

    public async Task<(BitrixWebhookValidationDto? Validation, string? Error)> ValidateAdminOfficeBitrixIntegrationAsync(
        Guid officeId,
        string? webhookUrl,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/admin/offices/{officeId:D}/integrations/bitrix/validate");
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

        return await MaterializeDownloadResponseAsync(response, "Orbita.Worker.Setup.msi", ct);
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

        return await MaterializeDownloadResponseAsync(response, $"Orbita.Worker.Setup-{version}.msi", ct);
    }

    private static async Task<(Stream? Stream, string? FileName, string? Error)> MaterializeDownloadResponseAsync(
        HttpResponseMessage response,
        string defaultFileName,
        CancellationToken ct)
    {
        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? defaultFileName;
        var extension = Path.GetExtension(defaultFileName);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 16)
        {
            extension = ".download";
        }
        var tempPath = Path.Combine(Path.GetTempPath(), $"orbita-download-{Guid.NewGuid():N}{extension}");

        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = File.Create(tempPath))
            {
                await input.CopyToAsync(output, ct);
            }

            var fileInfo = new FileInfo(tempPath);
            if (fileInfo.Length == 0)
            {
                TryDeleteDownloadTemp(tempPath);
                return (null, null, "Сервер вернул пустой файл.");
            }

            if (response.Content.Headers.ContentLength is > 0
                && fileInfo.Length != response.Content.Headers.ContentLength.Value)
            {
                TryDeleteDownloadTemp(tempPath);
                return (null, null, "Размер скачанного файла не совпал с ответом сервера.");
            }

            var stream = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            return (stream, fileName, null);
        }
        catch
        {
            TryDeleteDownloadTemp(tempPath);
            throw;
        }
    }

    private static void TryDeleteDownloadTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore cleanup errors
        }
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

        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.Forbidden => "Недостаточно прав для этой операции.",
            System.Net.HttpStatusCode.Unauthorized => InvalidApiSessionError,
            System.Net.HttpStatusCode.NotFound => "Объект не найден.",
            _ => "Не удалось выполнить операцию."
        };
    }

    public async Task<(CaptchaSessionDto? Session, CaptchaSessionConflictDto? Conflict, string? Error)> CreateCaptchaSessionAsync(
        CreateCaptchaSessionRequest request,
        CancellationToken ct = default)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/panel/captcha-sessions");
        httpRequest.Content = JsonContent.Create(request);
        using var response = await SendAuthenticatedAsync(httpRequest, ct);
        if (response is null)
        {
            return (null, null, InvalidApiSessionError);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync<CaptchaSessionConflictDto>(ApiJsonOptions, ct);
            return (null, conflict, conflict?.Message);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, null, await ReadApiErrorAsync(response, ct));
        }

        var session = await response.Content.ReadFromJsonAsync<CaptchaSessionDto>(ApiJsonOptions, ct);
        return (session, null, session is null ? "Пустой ответ API." : null);
    }

    public async Task<(bool Success, string? Error)> CancelCaptchaSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/panel/captcha-sessions/{sessionId:D}/cancel");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public Task<WorkerCaptchaLockDto?> GetWorkerCaptchaLockAsync(Guid workerId, CancellationToken ct = default) =>
        GetAsync<WorkerCaptchaLockDto>($"api/v1/panel/workers/{workerId:D}/captcha-lock", ct);

    public async Task<(BrowserMonitorSessionDto? Session, string? Error)> StartBrowserMonitorSessionAsync(
        Guid workerId,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/panel/browser-monitor-sessions?workerId={workerId:D}");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, await ReadApiErrorAsync(response, ct));
        }

        var session = await response.Content.ReadFromJsonAsync<BrowserMonitorSessionDto>(ApiJsonOptions, ct);
        return (session, session is null ? "Пустой ответ API." : null);
    }

    public async Task<(bool Success, string? Error)> StopBrowserMonitorSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/panel/browser-monitor-sessions/{sessionId:D}/stop");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (false, InvalidApiSessionError);
        }

        return response.IsSuccessStatusCode
            ? (true, null)
            : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<BrowserMonitorSessionDto?> GetBrowserMonitorSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await GetAsync<BrowserMonitorSessionDto>($"api/v1/panel/browser-monitor-sessions/{sessionId:D}", ct);
    }

    public async Task<CrmBoardDto?> GetCrmBoardAsync(
        Guid? officeId = null,
        CrmBoardQuery? query = null,
        CancellationToken ct = default)
    {
        var (board, _) = await GetCrmBoardResultAsync(officeId, query, ct);
        return board;
    }

    public Task<CrmAnalyticsDto?> GetCrmAnalyticsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        Guid? officeId = null,
        string? managerUserId = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return Task.FromResult<CrmAnalyticsDto?>(
                DesignPreviewData.GetCrmAnalytics(officeId ?? officeContext.EffectiveOfficeId, fromUtc, toUtc, managerUserId));
        }

        var url = WithOfficeQuery("api/v1/crm/analytics", officeId);
        url = AppendQuery(url, "fromUtc", fromUtc.ToUniversalTime().ToString("O"));
        url = AppendQuery(url, "toUtc", toUtc.ToUniversalTime().ToString("O"));
        url = AppendQuery(url, "managerUserId", managerUserId);
        return GetAsync<CrmAnalyticsDto>(url, ct);
    }

    /// <summary>
    /// Loads CRM board. ErrorCode: unauthorized | forbidden | bad_request | not_found | error | null on success.
    /// </summary>
    public async Task<(CrmBoardDto? Board, string? ErrorCode)> GetCrmBoardResultAsync(
        Guid? officeId = null,
        CrmBoardQuery? query = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (DesignPreviewData.GetCrmBoard(query), null);
        }

        query ??= new CrmBoardQuery();
        var url = WithOfficeQuery("api/v1/crm/board", officeId);
        url = AppendQuery(url, "search", query.Search);
        url = AppendQuery(url, "scopeFilter", query.Scope);
        url = AppendQuery(url, "managerUserId", query.ManagerUserId);
        url = AppendQuery(url, "city", query.City);
        url = AppendQuery(url, "vacancy", query.Vacancy);
        url = AppendQuery(url, "closeReason", query.CloseReason);
        url = AppendQuery(url, "stage", query.Stage);
        url = AppendQuery(url, "createdFromUtc", query.CreatedFromUtc?.ToString("O"));
        url = AppendQuery(url, "createdToUtc", query.CreatedToUtc?.ToString("O"));
        url = AppendQuery(url, "createdFrom", query.CreatedFrom);
        url = AppendQuery(url, "createdTo", query.CreatedTo);
        url = AppendQuery(url, "view", query.View);
        url = AppendQuery(url, "page", CrmBoardListOptions.NormalizePage(query.Page).ToString());
        url = AppendQuery(url, "pageSize", CrmBoardListOptions.NormalizePageSize(query.PageSize).ToString());
        url = AppendQuery(url, "sort", query.Sort);
        url = AppendQuery(url, "sortDir", query.SortDir);
        // Always send bools — older API builds rejected missing non-nullable query bools with 400.
        url = AppendQuery(url, "overdueOnly", query.OverdueOnly ? "true" : "false");
        url = AppendQuery(url, "activeLoadOnly", query.ActiveLoadOnly ? "true" : "false");
        url = AppendQuery(url, "includeClosed", query.IncludeClosed ? "true" : "false");
        url = AppendQuery(url, "timeZoneOffsetMinutes", query.TimeZoneOffsetMinutes.ToString());

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null)
        {
            return (null, "no_session");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return (null, "unauthorized");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return (null, "forbidden");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return (null, "not_found");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return (null, "bad_request");
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, "error");
        }

        try
        {
            var board = await response.Content.ReadFromJsonAsync<CrmBoardDto>(ApiJsonOptions, ct);
            return board is null ? (null, "error") : (board, null);
        }
        catch (JsonException)
        {
            return (null, "error");
        }
    }

    public Task<CrmCandidateDetailDto?> GetCrmCardAsync(Guid cardId, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult(DesignPreviewData.GetCrmCard(cardId))
            : GetAsync<CrmCandidateDetailDto>($"api/v1/crm/cards/{cardId:D}", ct);

    public async Task<(bool Success, string? Error)> DeleteCrmCardAsync(
        Guid cardId,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (false, "Удаление недоступно в режиме предпросмотра.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/crm/cards/{cardId:D}");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null
            ? (false, InvalidApiSessionError)
            : response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(Stream? Stream, string? ContentType)> GetCrmCardAvatarAsync(Guid cardId, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (null, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/crm/cards/{cardId:D}/avatar");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            return (null, null);
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var buffer = await response.Content.ReadAsByteArrayAsync(ct);
        return buffer.Length == 0 ? (null, contentType) : (new MemoryStream(buffer), contentType);
    }

    public Task<IReadOnlyList<CrmTaskDto>?> GetCrmTasksAsync(
        Guid? officeId = null,
        CancellationToken ct = default,
        string? managerUserId = null) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<CrmTaskDto>?>(DesignPreviewData.GetCrmTasks(managerUserId))
            : GetAsync<IReadOnlyList<CrmTaskDto>>(
                AppendQuery(WithOfficeQuery("api/v1/crm/tasks", officeId), "managerUserId", managerUserId),
                ct);

    public Task<IReadOnlyList<CrmManagerDto>?> GetCrmTaskManagersAsync(Guid? officeId = null, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<IReadOnlyList<CrmManagerDto>?>(DesignPreviewData.GetCrmBoard().Managers)
            : GetAsync<IReadOnlyList<CrmManagerDto>>(WithOfficeQuery("api/v1/crm/tasks/managers", officeId), ct);

    public Task<CrmTaskDetailDto?> GetCrmTaskAsync(Guid taskId, CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult(DesignPreviewData.GetCrmTask(taskId))
            : GetAsync<CrmTaskDetailDto>($"api/v1/crm/tasks/{taskId:D}", ct);

    public Task<CrmTaskNotificationsDto?> GetCrmTaskNotificationsAsync(
        bool unreadOnly = false,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return Task.FromResult<CrmTaskNotificationsDto?>(
                DesignPreviewData.GetCrmTaskNotifications(unreadOnly, limit));
        }

        var url = WithOfficeQuery("api/v1/crm/notifications");
        url = AppendQuery(url, "unreadOnly", unreadOnly ? "true" : "false");
        url = AppendQuery(url, "limit", Math.Clamp(limit, 1, 50).ToString());
        return GetAsync<CrmTaskNotificationsDto>(url, ct);
    }

    public Task<CrmTaskNotificationSummaryDto?> GetCrmTaskNotificationSummaryAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<CrmTaskNotificationSummaryDto?>(DesignPreviewData.GetCrmTaskNotificationSummary())
            : GetAsync<CrmTaskNotificationSummaryDto>(WithOfficeQuery("api/v1/crm/notifications/summary"), ct);

    public async Task<(bool Success, string? Error)> MarkCrmTaskNotificationReadAsync(
        Guid notificationId,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.MarkCrmTaskNotificationRead(notificationId);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            WithOfficeQuery($"api/v1/crm/notifications/{notificationId:D}/read"));
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null
            ? (false, InvalidApiSessionError)
            : response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> MarkAllCrmTaskNotificationsReadAsync(CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.MarkAllCrmTaskNotificationsRead();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            WithOfficeQuery("api/v1/crm/notifications/read-all"));
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null
            ? (false, InvalidApiSessionError)
            : response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(CrmTaskAttachmentDto? Attachment, string? Error)> UploadCrmTaskAttachmentAsync(
        Guid taskId,
        Stream content,
        long contentLength,
        string fileName,
        string? contentType,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.AddCrmTaskAttachment(taskId, content, contentLength, fileName, contentType);
        }

        using var form = new MultipartFormDataContent();
        using var file = new StreamContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            ? mediaType
            : new MediaTypeHeaderValue("application/octet-stream");
        file.Headers.ContentLength = contentLength;
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/tasks/{taskId:D}/attachments")
        {
            Content = form
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null) return (null, InvalidApiSessionError);
        if (!response.IsSuccessStatusCode) return (null, await ReadApiErrorAsync(response, ct));
        var attachment = await response.Content.ReadFromJsonAsync<CrmTaskAttachmentDto>(ApiJsonOptions, ct);
        return attachment is null ? (null, "Не удалось прочитать ответ API.") : (attachment, null);
    }

    public async Task<(Stream? Stream, string? FileName, string? ContentType, string? Error)> OpenCrmTaskAttachmentAsync(
        Guid taskId,
        Guid attachmentId,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            var attachment = DesignPreviewData.OpenCrmTaskAttachment(taskId, attachmentId);
            return (attachment.Stream, attachment.FileName, attachment.ContentType, attachment.Stream is null ? "Вложение не найдено." : null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/crm/tasks/{taskId:D}/attachments/{attachmentId:D}");
        using var response = await SendAuthenticatedAsync(request, ct, HttpCompletionOption.ResponseHeadersRead);
        if (response is null)
        {
            return (null, null, null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, null, null, await ReadApiErrorAsync(response, ct));
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var (stream, fileName, error) = await MaterializeDownloadResponseAsync(response, "Вложение", ct);
        return (stream, fileName, contentType, error);
    }

    public async Task<(Stream? Stream, string? FileName, string? ContentType, string? Error)> OpenCrmCallRecordingAsync(
        Guid callId,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (null, null, null, "Запись звонка недоступна в режиме предпросмотра.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/crm/calls/{callId:D}/recording");
        using var response = await SendAuthenticatedAsync(request, ct, HttpCompletionOption.ResponseHeadersRead);
        if (response is null)
        {
            return (null, null, null, InvalidApiSessionError);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, null, null, await ReadApiErrorAsync(response, ct));
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/wav";
        var (stream, fileName, error) = await MaterializeDownloadResponseAsync(response, $"Звонок-{callId:N}.wav", ct);
        return (stream, fileName, contentType, error);
    }

    public Task<(bool Success, string? Error)> StartCrmShiftAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult(DesignPreviewData.StartCrmShift())
            : PostPanelActionAsync("api/v1/crm/shift/start", ct);

    public Task<(bool Success, string? Error)> StopCrmShiftAsync(CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult(DesignPreviewData.StopCrmShift())
            : PostPanelActionAsync("api/v1/crm/shift/stop", ct);

    public async Task<(bool Success, string? Error)> MoveCrmCardAsync(Guid cardId, string stage, string? comment = null, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.MoveCrmCard(cardId, stage, comment);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/move")
        {
            Content = JsonContent.Create(new CrmMoveRequest(stage, comment))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> SetCrmCardActiveLoadAsync(Guid cardId, bool active, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.SetCrmCardActiveLoad(cardId, active);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/active-load/{active.ToString().ToLowerInvariant()}");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> AssignCrmCardAsync(Guid cardId, string managerUserId, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.AssignCrmCard(cardId, managerUserId);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/assign")
        {
            Content = JsonContent.Create(new CrmAssignRequest(managerUserId))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(CrmBulkActionResult? Result, string? Error)> BulkAssignCrmCardsAsync(
        CrmBulkAssignRequest body,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            var updated = 0;
            var errors = new List<string>();
            foreach (var cardId in body.CardIds.Where(id => id != Guid.Empty).Distinct().Take(500))
            {
                var (success, error) = DesignPreviewData.AssignCrmCard(cardId, body.ManagerUserId);
                if (success) updated++;
                else errors.Add(error ?? "Не удалось изменить ответственного у одной из карточек.");
            }

            var requested = body.CardIds.Where(id => id != Guid.Empty).Distinct().Take(500).Count();
            return (new CrmBulkActionResult(
                requested,
                updated,
                requested - updated,
                errors.Distinct(StringComparer.Ordinal).Take(3).ToArray()), null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/crm/cards/bulk/assign")
        {
            Content = JsonContent.Create(body)
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null) return (null, InvalidApiSessionError);
        if (!response.IsSuccessStatusCode) return (null, await ReadApiErrorAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<CrmBulkActionResult>(ApiJsonOptions, ct), null);
    }

    public async Task<(CrmBulkActionResult? Result, string? Error)> BulkTransitionCrmCardsAsync(
        CrmBulkTransitionRequest body,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            var updated = 0;
            var errors = new List<string>();
            var cardIds = body.CardIds.Where(id => id != Guid.Empty).Distinct().Take(500).ToArray();
            foreach (var cardId in cardIds)
            {
                var result = body.Operation == CrmBulkTransitionOperations.Close
                    ? DesignPreviewData.CloseCrmCard(cardId, body.CloseReason ?? string.Empty, body.Comment)
                    : DesignPreviewData.MoveCrmCard(cardId, body.Stage ?? string.Empty, body.Comment);
                if (result.Success) updated++;
                else errors.Add(result.Error ?? "Не удалось изменить одну из карточек.");
            }

            return (new CrmBulkActionResult(
                cardIds.Length,
                updated,
                cardIds.Length - updated,
                errors.Distinct(StringComparer.Ordinal).Take(3).ToArray()), null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/crm/cards/bulk/transition")
        {
            Content = JsonContent.Create(body)
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null) return (null, InvalidApiSessionError);
        if (!response.IsSuccessStatusCode) return (null, await ReadApiErrorAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<CrmBulkActionResult>(ApiJsonOptions, ct), null);
    }

    public async Task<(bool Success, string? Error)> UpdateCrmCardAsync(Guid cardId, CrmCardUpdateRequest body, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.UpdateCrmCard(cardId, body);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/crm/cards/{cardId:D}")
        {
            Content = JsonContent.Create(body)
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> AddCrmContactPhoneAsync(
        Guid cardId,
        string phoneRaw,
        string? label = null,
        bool setAsPrimary = false,
        CancellationToken ct = default)
    {
        if (_preview.Enabled) return (true, null);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/phones")
        {
            Content = JsonContent.Create(new CrmContactPhoneCreateRequest(phoneRaw, label, setAsPrimary))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> RemoveCrmContactPhoneAsync(Guid cardId, Guid phoneId, CancellationToken ct = default)
    {
        if (_preview.Enabled) return (true, null);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/crm/cards/{cardId:D}/phones/{phoneId:D}");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> SetCrmPrimaryPhoneAsync(Guid cardId, Guid phoneId, CancellationToken ct = default)
    {
        if (_preview.Enabled) return (true, null);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/phones/{phoneId:D}/primary");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> MarkCrmChatReadAsync(Guid cardId, CancellationToken ct = default)
    {
        if (_preview.Enabled) return (true, null);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/chat/read");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> QueueCrmChatMessageAsync(Guid cardId, string text, CancellationToken ct = default)
    {
        if (_preview.Enabled) return (true, null);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/chat")
        {
            Content = JsonContent.Create(new CrmChatSendRequest(text))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> CancelCrmChatMessageAsync(Guid cardId, Guid messageId, CancellationToken ct = default)
    {
        if (_preview.Enabled) return (true, null);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/chat/{messageId:D}/cancel");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(Guid? CardId, string? Error)> CreateManualCrmCardAsync(CrmManualCardCreateRequest body, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (Guid.NewGuid(), null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, WithOfficeQuery("api/v1/crm/cards/manual"))
        {
            Content = JsonContent.Create(body)
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null) return (null, InvalidApiSessionError);
        if (!response.IsSuccessStatusCode) return (null, await ReadApiErrorAsync(response, ct));
        var payload = await response.Content.ReadFromJsonAsync<CrmManualCardCreateResult>(ApiJsonOptions, ct);
        return payload is null || payload.Id == Guid.Empty
            ? (null, "Карточка создана, но ответ API не распознан.")
            : (payload.Id, null);
    }

    public async Task<(CrmLeadFileImportResult? Result, string? Error)> ImportCrmLeadFileAsync(
        CrmLeadFileImportRequest body,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return (new CrmLeadFileImportResult(
                body.Entries.Count,
                body.Entries.Count,
                0,
                body.DuplicateRowsInFile,
                body.Entries.Count,
                1), null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, WithOfficeQuery("api/v1/crm/cards/import-file"))
        {
            Content = JsonContent.Create(body)
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null) return (null, InvalidApiSessionError);
        if (!response.IsSuccessStatusCode) return (null, await ReadApiErrorAsync(response, ct));
        var payload = await response.Content.ReadFromJsonAsync<CrmLeadFileImportResult>(ApiJsonOptions, ct);
        return payload is null
            ? (null, "Импорт выполнен, но ответ API не распознан.")
            : (payload, null);
    }

    public async Task<(bool Success, string? Error)> CloseCrmCardAsync(Guid cardId, string reason, string? comment = null, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.CloseCrmCard(cardId, reason, comment);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/close")
        {
            Content = JsonContent.Create(new CrmCloseRequest(reason, comment))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> ReopenCrmCardAsync(Guid cardId, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.ReopenCrmCard(cardId);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/reopen");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> AddCrmNoteAsync(Guid cardId, string text, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.AddCrmNote(cardId, text);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/notes") { Content = JsonContent.Create(new CrmNoteCreateRequest(text)) };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> UpdateCrmNoteAsync(
        Guid cardId,
        Guid noteId,
        string text,
        CancellationToken ct = default)
    {
        if (_preview.Enabled) return DesignPreviewData.UpdateCrmNote(cardId, noteId, text);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/crm/cards/{cardId:D}/notes/{noteId:D}")
        {
            Content = JsonContent.Create(new CrmNoteUpdateRequest(text))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> DeleteCrmNoteAsync(
        Guid cardId,
        Guid noteId,
        CancellationToken ct = default)
    {
        if (_preview.Enabled) return DesignPreviewData.DeleteCrmNote(cardId, noteId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/crm/cards/{cardId:D}/notes/{noteId:D}");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> SetCrmNotePinnedAsync(
        Guid cardId,
        Guid noteId,
        bool isPinned,
        CancellationToken ct = default)
    {
        if (_preview.Enabled) return DesignPreviewData.SetCrmNotePinned(cardId, noteId, isPinned);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/notes/{noteId:D}/pin")
        {
            Content = JsonContent.Create(new CrmNotePinRequest(isPinned))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(CrmTaskDto? Task, string? Error)> CreateCrmTaskAsync(CrmTaskCreateRequest task, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.CreateCrmTask(task);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, WithOfficeQuery("api/v1/crm/tasks")) { Content = JsonContent.Create(task) };
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null) return (null, InvalidApiSessionError);
        if (!response.IsSuccessStatusCode) return (null, await ReadApiErrorAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<CrmTaskDto>(ApiJsonOptions, ct), null);
    }

    public async Task<(CrmTaskDto? Task, string? Error)> CreateCrmFollowUpAsync(Guid cardId, int minutes, string? title = null, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.CreateCrmFollowUp(cardId, minutes, title);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/cards/{cardId:D}/follow-up")
        {
            Content = JsonContent.Create(new CrmFollowUpRequest(minutes, title))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null) return (null, InvalidApiSessionError);
        if (!response.IsSuccessStatusCode) return (null, await ReadApiErrorAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<CrmTaskDto>(ApiJsonOptions, ct), null);
    }

    public async Task<(bool Success, string? Error)> CompleteCrmTaskAsync(Guid taskId, string comment, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.CompleteCrmTask(taskId, comment);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/tasks/{taskId:D}/complete")
        {
            Content = JsonContent.Create(new CrmTaskCompleteRequest(comment))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> UpdateCrmTaskAsync(
        Guid taskId,
        CrmTaskUpdateRequest update,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.UpdateCrmTask(taskId, update);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/crm/tasks/{taskId:D}")
        {
            Content = JsonContent.Create(update)
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> CancelCrmTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.CancelCrmTask(taskId);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/tasks/{taskId:D}/cancel");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> ReopenCrmTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.ReopenCrmTask(taskId);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/tasks/{taskId:D}/reopen");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> DeleteCrmTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        if (_preview.Enabled) return DesignPreviewData.DeleteCrmTask(taskId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/crm/tasks/{taskId:D}");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> AddCrmTaskCommentAsync(Guid taskId, string text, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.AddCrmTaskComment(taskId, text);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/crm/tasks/{taskId:D}/comments")
        {
            Content = JsonContent.Create(new CrmTaskCommentCreateRequest(text))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> UpdateCrmTaskCommentAsync(
        Guid taskId,
        Guid commentId,
        string text,
        CancellationToken ct = default)
    {
        if (_preview.Enabled) return DesignPreviewData.UpdateCrmTaskComment(taskId, commentId, text);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/crm/tasks/{taskId:D}/comments/{commentId:D}")
        {
            Content = JsonContent.Create(new CrmTaskCommentUpdateRequest(text))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> DeleteCrmTaskCommentAsync(
        Guid taskId,
        Guid commentId,
        CancellationToken ct = default)
    {
        if (_preview.Enabled) return DesignPreviewData.DeleteCrmTaskComment(taskId, commentId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/crm/tasks/{taskId:D}/comments/{commentId:D}");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> SetCrmOfficeSettingsAsync(
        Guid officeId,
        bool enabled,
        bool requireStageComment,
        bool? deadlineNotificationsEnabled = null,
        CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.SetCrmOfficeSettings(
                enabled,
                requireStageComment,
                deadlineNotificationsEnabled);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/crm/offices/{officeId:D}/settings")
        {
            Content = JsonContent.Create(new CrmOfficeSettingsRequest(
                enabled,
                requireStageComment,
                deadlineNotificationsEnabled))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public Task<CrmTelephonySettingsDto?> GetCrmTelephonySettingsAsync(
        Guid officeId,
        CancellationToken ct = default,
        string provider = CrmTelephonyProviders.Sipout) =>
        _preview.Enabled
            ? Task.FromResult<CrmTelephonySettingsDto?>(new CrmTelephonySettingsDto(
                officeId, provider, false, false, null, []))
            : GetAsync<CrmTelephonySettingsDto>($"api/v1/crm/telephony/offices/{officeId:D}/{Uri.EscapeDataString(provider)}", ct);

    public Task<CrmTelephonyWebRtcConfigDto?> GetCrmTelephonyWebRtcConfigAsync(
        CancellationToken ct = default) =>
        _preview.Enabled
            ? Task.FromResult<CrmTelephonyWebRtcConfigDto?>(null)
            : GetAsync<CrmTelephonyWebRtcConfigDto>(
                WithOfficeQuery("api/v1/crm/telephony/webrtc/config"),
                ct);

    public async Task<(CrmTelephonyReceiverDto? Receiver, string? Error)> RotateCrmTelephonyReceiverAsync(
        Guid officeId,
        CancellationToken ct = default,
        string provider = CrmTelephonyProviders.Sipout)
    {
        if (_preview.Enabled)
        {
            return (null, "Настройка телефонии недоступна в режиме предпросмотра.");
        }
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/crm/telephony/offices/{officeId:D}/{Uri.EscapeDataString(provider)}/receiver");
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null) return (null, InvalidApiSessionError);
        if (!response.IsSuccessStatusCode) return (null, await ReadApiErrorAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<CrmTelephonyReceiverDto>(ApiJsonOptions, ct), null);
    }

    public async Task<(bool Success, string? Error)> SetCrmTelephonyEnabledAsync(
        Guid officeId,
        bool enabled,
        CancellationToken ct = default,
        string provider = CrmTelephonyProviders.Sipout)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"api/v1/crm/telephony/offices/{officeId:D}/{Uri.EscapeDataString(provider)}/enabled")
        {
            Content = JsonContent.Create(new UpdateCrmTelephonyEnabledRequest(enabled))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null
            ? (false, InvalidApiSessionError)
            : response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> SetPlusofonCredentialsAsync(
        Guid officeId,
        string clientId,
        string accessToken,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"api/v1/crm/telephony/offices/{officeId:D}/{CrmTelephonyProviders.Plusofon}/credentials")
        {
            Content = JsonContent.Create(new UpdatePlusofonCredentialsRequest(clientId, accessToken))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null
            ? (false, InvalidApiSessionError)
            : response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> SetSipProviderAccountAsync(
        Guid officeId,
        string provider,
        UpdateSipProviderAccountRequest account,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"api/v1/crm/telephony/offices/{officeId:D}/{Uri.EscapeDataString(provider)}/sip-account")
        {
            Content = JsonContent.Create(account)
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null
            ? (false, InvalidApiSessionError)
            : response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> AddSipProviderAccountAsync(
        Guid officeId,
        string provider,
        UpdateSipProviderAccountRequest account,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/crm/telephony/offices/{officeId:D}/{Uri.EscapeDataString(provider)}/sip-accounts")
        {
            Content = JsonContent.Create(account)
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null
            ? (false, InvalidApiSessionError)
            : response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> UpdateSipProviderAccountAsync(
        Guid officeId,
        string provider,
        string accountKey,
        UpdateSipProviderAccountRequest account,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"api/v1/crm/telephony/offices/{officeId:D}/{Uri.EscapeDataString(provider)}/sip-accounts/{Uri.EscapeDataString(accountKey)}")
        {
            Content = JsonContent.Create(account)
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null
            ? (false, InvalidApiSessionError)
            : response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> DeleteSipProviderAccountAsync(
        Guid officeId,
        string provider,
        string accountKey,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"api/v1/crm/telephony/offices/{officeId:D}/{Uri.EscapeDataString(provider)}/sip-accounts/{Uri.EscapeDataString(accountKey)}");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null
            ? (false, InvalidApiSessionError)
            : response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> SetOfficeDefaultOutboundAsync(
        Guid officeId,
        string outboundProvider,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"api/v1/crm/telephony/offices/{officeId:D}/outbound-default")
        {
            Content = JsonContent.Create(new UpdateCrmTelephonyOfficeOutboundRequest(outboundProvider))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null
            ? (false, InvalidApiSessionError)
            : response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(CrmTelephonyUserBindingDto? Binding, string? Error)> SetCrmTelephonyBindingAsync(
        Guid officeId,
        string userId,
        string providerUserKey,
        CancellationToken ct = default,
        string provider = CrmTelephonyProviders.Sipout,
        string? outboundProvider = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"api/v1/crm/telephony/offices/{officeId:D}/{Uri.EscapeDataString(provider)}/bindings")
        {
            Content = JsonContent.Create(new UpdateCrmTelephonyBindingRequest(userId, providerUserKey, outboundProvider))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        if (response is null) return (null, InvalidApiSessionError);
        if (!response.IsSuccessStatusCode) return (null, await ReadApiErrorAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<CrmTelephonyUserBindingDto>(ApiJsonOptions, ct), null);
    }

    public async Task<(bool Success, string? Error)> RemoveCrmTelephonyBindingAsync(
        Guid officeId,
        string userId,
        CancellationToken ct = default,
        string provider = CrmTelephonyProviders.Sipout)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"api/v1/crm/telephony/offices/{officeId:D}/{Uri.EscapeDataString(provider)}/bindings/{Uri.EscapeDataString(userId)}");
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null
            ? (false, InvalidApiSessionError)
            : response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> SetCrmOfficeFunnelAsync(Guid officeId, IReadOnlyList<string> stages, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.SetCrmOfficeFunnel(stages);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/crm/offices/{officeId:D}/funnel")
        {
            Content = JsonContent.Create(new CrmOfficeFunnelRequest(stages))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    public async Task<(bool Success, string? Error)> SetCrmManagerCapacityAsync(string managerUserId, int capacity, Guid? officeId = null, CancellationToken ct = default)
    {
        if (_preview.Enabled)
        {
            return DesignPreviewData.SetCrmManagerCapacity(managerUserId, capacity);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, WithOfficeQuery($"api/v1/crm/managers/{Uri.EscapeDataString(managerUserId)}/capacity", officeId))
        {
            Content = JsonContent.Create(new CrmCapacityRequest(capacity))
        };
        using var response = await SendAuthenticatedAsync(request, ct);
        return response is null ? (false, InvalidApiSessionError) : response.IsSuccessStatusCode ? (true, null) : (false, await ReadApiErrorAsync(response, ct));
    }

    private static string AppendQuery(string url, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return url;
        }

        var sep = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{url}{sep}{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
    }

    private sealed record ApiErrorResponse(string? Error);
}

public sealed record LoginResponse(string Token, string Email);

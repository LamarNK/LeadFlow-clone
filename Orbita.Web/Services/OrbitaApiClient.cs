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

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(session.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        }
    }

}

public sealed record LoginResponse(string Token, string Email);
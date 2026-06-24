using System.Net.Http.Headers;
using System.Net.Http.Json;
using Orbita.Contracts;

namespace Orbita.Web.Services;

public sealed class OrbitaApiClient(HttpClient http, AuthSession session)
{
    public async Task<LoginResponse?> LoginAsync(string email, string password, CancellationToken ct = default)
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

    public async Task<GlobalDashboardSummary?> GetSummaryAsync(CancellationToken ct = default) =>
        await GetAsync<GlobalDashboardSummary>("api/v1/dashboard/summary", ct);

    public async Task<IReadOnlyList<WorkerListItem>?> GetWorkersAsync(CancellationToken ct = default) =>
        await GetAsync<IReadOnlyList<WorkerListItem>>("api/v1/workers", ct);

    public async Task<WorkerDetail?> GetWorkerAsync(Guid id, CancellationToken ct = default) =>
        await GetAsync<WorkerDetail>($"api/v1/workers/{id}", ct);

    public async Task<IReadOnlyList<WorkerAccountDto>?> GetWorkerAccountsAsync(Guid id, CancellationToken ct = default) =>
        await GetAsync<IReadOnlyList<WorkerAccountDto>>($"api/v1/workers/{id}/accounts", ct);

    public async Task<IReadOnlyList<WorkerEventListItem>?> GetEventsAsync(Guid? workerId = null, int limit = 100, CancellationToken ct = default)
    {
        var url = workerId.HasValue
            ? $"api/v1/events?workerId={workerId}&limit={limit}"
            : $"api/v1/events?limit={limit}";
        return await GetAsync<IReadOnlyList<WorkerEventListItem>>(url, ct);
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
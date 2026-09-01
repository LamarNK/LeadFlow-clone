using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbita.Api.Services;

public enum PlusofonRecordingOutcome
{
    Ready,
    NotReady,
    Unauthorized,
    TransientFailure
}

public sealed record PlusofonRecordingResult(
    PlusofonRecordingOutcome Outcome,
    string? RecordingUrl = null);

public sealed record PlusofonCallItem(
    string CallId,
    string? NumberA,
    string? NumberB,
    string? FinalNumber,
    string? Direction,
    string? ProviderUserKey,
    string? ConnectedAt,
    string? DurationSeconds,
    string? RecordingUrl);

public sealed record PlusofonCallPage(
    PlusofonRecordingOutcome Outcome,
    IReadOnlyList<PlusofonCallItem> Calls,
    bool HasMore);

public interface IPlusofonApiClient
{
    Task<PlusofonCallPage> GetCallsAsync(
        string clientId,
        string accessToken,
        DateTime fromUtc,
        DateTime toUtc,
        int page,
        CancellationToken ct = default) => Task.FromResult(
            new PlusofonCallPage(PlusofonRecordingOutcome.NotReady, [], false));

    Task<PlusofonRecordingResult> GetRecordingAsync(
        string clientId,
        string accessToken,
        string callId,
        CancellationToken ct = default);
}

public sealed class PlusofonApiClient(IHttpClientFactory httpClientFactory) : IPlusofonApiClient
{
    public const string HttpClientName = "PlusofonApi";

    public async Task<PlusofonCallPage> GetCallsAsync(
        string clientId,
        string accessToken,
        DateTime fromUtc,
        DateTime toUtc,
        int page,
        CancellationToken ct = default)
    {
        var from = Uri.EscapeDataString(fromUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        var to = Uri.EscapeDataString(toUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/v1/call?date_from={from}&date_to={to}&direction=all&page={Math.Max(1, page)}&limit=100");
        AddCredentials(request, clientId, accessToken);
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new PlusofonCallPage(PlusofonRecordingOutcome.Unauthorized, [], false);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new PlusofonCallPage(PlusofonRecordingOutcome.TransientFailure, [], false);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return new PlusofonCallPage(PlusofonRecordingOutcome.NotReady, [], false);
            }

            var calls = new List<PlusofonCallItem>();
            foreach (var item in data.EnumerateArray())
            {
                var numberA = ReadString(item, "number_a", "a", "CLI", "cli");
                var numberB = ReadString(item, "number_b", "b");
                var finalNumber = ReadString(item, "cld", "CLD");
                var connectedAt = ReadString(item, "connect_time", "started_at", "start_time");
                var duration = ReadString(item, "duration", "billsec");
                var account = ReadString(item, "account", "sip_id", "i_account", "internal");
                var direction = ReadString(item, "direction", "type_call");
                var record = ReadString(item, "record", "recording_url", "record_url");
                var callId = ReadString(item, "call_id", "id", "uuid", "bridge_id");
                if (string.IsNullOrWhiteSpace(callId))
                {
                    callId = BuildStableCallId(account, connectedAt, numberA, numberB, finalNumber, duration);
                }
                calls.Add(new PlusofonCallItem(
                    callId,
                    numberA,
                    numberB ?? finalNumber,
                    finalNumber,
                    direction,
                    account,
                    connectedAt,
                    duration,
                    NormalizeHttpsUrl(record)));
            }

            var currentPage = ReadInt(root, "current_page") ?? Math.Max(1, page);
            var lastPage = ReadInt(root, "last_page");
            var hasMore = lastPage is int totalPages
                ? currentPage < totalPages
                : calls.Count >= 100;
            return new PlusofonCallPage(PlusofonRecordingOutcome.Ready, calls, hasMore);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PlusofonCallPage(PlusofonRecordingOutcome.TransientFailure, [], false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return new PlusofonCallPage(PlusofonRecordingOutcome.TransientFailure, [], false);
        }
    }

    public async Task<PlusofonRecordingResult> GetRecordingAsync(
        string clientId,
        string accessToken,
        string callId,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/v1/call/{Uri.EscapeDataString(callId)}/record");
        AddCredentials(request, clientId, accessToken);

        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new PlusofonRecordingResult(PlusofonRecordingOutcome.Unauthorized);
            }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            {
                return new PlusofonRecordingResult(PlusofonRecordingOutcome.NotReady);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new PlusofonRecordingResult(PlusofonRecordingOutcome.TransientFailure);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;
            var success = !root.TryGetProperty("success", out var successElement)
                || successElement.ValueKind != JsonValueKind.False;
            var record = root.TryGetProperty("record", out var recordElement)
                && recordElement.ValueKind == JsonValueKind.String
                    ? recordElement.GetString()
                    : null;
            if (success
                && Uri.TryCreate(record, UriKind.Absolute, out var recordingUri)
                && recordingUri.Scheme == Uri.UriSchemeHttps)
            {
                return new PlusofonRecordingResult(
                    PlusofonRecordingOutcome.Ready,
                    recordingUri.AbsoluteUri);
            }

            return new PlusofonRecordingResult(PlusofonRecordingOutcome.NotReady);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PlusofonRecordingResult(PlusofonRecordingOutcome.TransientFailure);
        }
        catch (HttpRequestException)
        {
            return new PlusofonRecordingResult(PlusofonRecordingOutcome.TransientFailure);
        }
        catch (JsonException)
        {
            return new PlusofonRecordingResult(PlusofonRecordingOutcome.TransientFailure);
        }
    }

    private static void AddCredentials(HttpRequestMessage request, string clientId, string accessToken)
    {
        request.Headers.TryAddWithoutValidation("Client", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : null;
    }

    private static string BuildStableCallId(params string?[] values)
    {
        var source = string.Join('|', values.Select(x => x?.Trim() ?? string.Empty));
        return $"import-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()}";
    }

    private static string? NormalizeHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;
}

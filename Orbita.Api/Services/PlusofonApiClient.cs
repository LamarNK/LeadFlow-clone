using System.Net;
using System.Net.Http.Headers;
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

public interface IPlusofonApiClient
{
    Task<PlusofonRecordingResult> GetRecordingAsync(
        string clientId,
        string accessToken,
        string callId,
        CancellationToken ct = default);
}

public sealed class PlusofonApiClient(IHttpClientFactory httpClientFactory) : IPlusofonApiClient
{
    public const string HttpClientName = "PlusofonApi";

    public async Task<PlusofonRecordingResult> GetRecordingAsync(
        string clientId,
        string accessToken,
        string callId,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/v1/call/{Uri.EscapeDataString(callId)}/record");
        request.Headers.TryAddWithoutValidation("Client", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

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
}

using System.Net;
using System.Net.Http.Headers;

namespace Orbita.TelephonyGateway;

public sealed record GatewayDeliveryResult(
    HttpStatusCode StatusCode,
    byte[] Body,
    string? ContentType,
    bool ShouldRetry,
    string? Error = null)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and <= 299;
}

public sealed class GatewayForwarder(HttpClient httpClient)
{
    private const int MaxResponseBytes = 64 * 1024;

    public async Task<GatewayDeliveryResult> ForwardAsync(
        GatewayWebhookJob job,
        CancellationToken ct = default,
        string pathSuffix = "")
    {
        var relativePath = $"api/v1/integrations/telephony/{job.Provider}/{job.PublicId:D}{pathSuffix}{job.QueryString}";
        using var request = new HttpRequestMessage(new HttpMethod(job.Method), relativePath);
        foreach (var header in job.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        var body = job.GetBody();
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            if (!string.IsNullOrWhiteSpace(job.ContentType)
                && MediaTypeHeaderValue.TryParse(job.ContentType, out var contentType))
            {
                request.Content.Headers.ContentType = contentType;
            }
        }

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            var responseBody = await ReadLimitedAsync(response.Content, MaxResponseBytes, ct);
            return new GatewayDeliveryResult(
                response.StatusCode,
                responseBody,
                response.Content.Headers.ContentType?.ToString(),
                ShouldRetry(response.StatusCode));
        }
        catch (HttpRequestException exception)
        {
            return new GatewayDeliveryResult(
                HttpStatusCode.ServiceUnavailable,
                [],
                null,
                ShouldRetry: true,
                Error: exception.Message);
        }
        catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
        {
            return new GatewayDeliveryResult(
                HttpStatusCode.GatewayTimeout,
                [],
                null,
                ShouldRetry: true,
                Error: exception.Message);
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        var value = (int)statusCode;
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || value is 425
            || value >= 500;
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken ct)
    {
        await using var source = await content.ReadAsStreamAsync(ct);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (destination.Length < maxBytes)
        {
            var remaining = maxBytes - (int)destination.Length;
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return destination.ToArray();
    }
}

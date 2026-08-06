using System.Net.Http.Headers;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

/// <summary>Downloads an Avito candidate avatar so the panel never needs to expose a CDN URL.</summary>
public sealed class AvitoAvatarDownloader(IHttpClientFactory httpClientFactory)
{
    public async Task<DownloadedAvatar?> DownloadAsync(string? avatarUrl, CancellationToken ct)
    {
        if (!TryGetAvitoImageUri(avatarUrl, out var uri))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));

            var client = httpClientFactory.CreateClient(nameof(AvitoAvatarDownloader));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength is > CandidateResponseAvatar.MaxImageBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var bytes = await ReadImageAsync(stream, ct).ConfigureAwait(false);
            var contentType = CandidateResponseAvatar.DetectContentType(bytes);
            return string.IsNullOrEmpty(contentType)
                ? null
                : new DownloadedAvatar(contentType, bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static bool TryGetAvitoImageUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(parsed.Host))
        {
            return false;
        }

        var host = parsed.Host.ToLowerInvariant();
        if ((host != "img.avito.st" && !host.EndsWith(".img.avito.st", StringComparison.Ordinal))
            || !parsed.AbsolutePath.StartsWith("/image/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static async Task<byte[]> ReadImageAsync(Stream stream, CancellationToken ct)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > CandidateResponseAvatar.MaxImageBytes)
            {
                return [];
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }
}

public sealed record DownloadedAvatar(string ContentType, byte[] Bytes);

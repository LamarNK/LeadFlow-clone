using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class ProviderRecordingArchiveService(
    OrbitaDbContext db,
    IHttpClientFactory httpClientFactory,
    CrmCallRecordingStorageService storage,
    IOptions<CrmCallRecordingOptions> options,
    TimeProvider timeProvider,
    ILogger<ProviderRecordingArchiveService> logger,
    IPanelRealtimeNotifier? panelRealtime = null)
{
    public const string HttpClientName = "ProviderRecordingArchive";
    private const int BatchSize = 20;
    private const int MaxAttempts = 20;
    private readonly CrmCallRecordingOptions _options = options.Value;

    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var calls = await db.CrmCalls
            .Where(x => x.RecordingStoragePath == null
                && x.RecordingUrl != null
                && x.RecordingArchiveAttempts < MaxAttempts
                && x.NextRecordingArchiveAtUtc != null
                && x.NextRecordingArchiveAtUtc <= now)
            .OrderBy(x => x.NextRecordingArchiveAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct);
        var archived = 0;
        foreach (var call in calls)
        {
            call.RecordingArchiveAttempts++;
            call.UpdatedAtUtc = now;
            if (!TryValidateUrl(call.RecordingUrl, out var uri))
            {
                call.NextRecordingArchiveAtUtc = null;
                logger.LogWarning("Rejected recording URL for call {CallId}: host is not allowed.", call.Id);
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await httpClientFactory.CreateClient(HttpClientName)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    call.NextRecordingArchiveAtUtc = now.Add(GetRetryDelay(call.RecordingArchiveAttempts));
                    continue;
                }
                if (response.Content.Headers.ContentLength is long length
                    && (length <= 0 || length > storage.MaxUploadBytes))
                {
                    call.NextRecordingArchiveAtUtc = null;
                    continue;
                }

                await using var content = await response.Content.ReadAsStreamAsync(ct);
                call.RecordingStoragePath = await storage.SaveAsync(call.Id, content, ct);
                call.RecordingContentType = NormalizeContentType(response.Content.Headers.ContentType?.MediaType);
                call.RecordingFileName = BuildFileName(uri, call.Id, call.RecordingContentType);
                call.NextRecordingArchiveAtUtc = null;
                archived++;
                if (call.CardId is not null)
                {
                    panelRealtime?.Notify([PanelChangeKind.Crm], call.OfficeId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                call.NextRecordingArchiveAtUtc = call.RecordingArchiveAttempts >= MaxAttempts
                    ? null
                    : now.Add(GetRetryDelay(call.RecordingArchiveAttempts));
                logger.LogWarning(ex, "Cannot archive provider recording for call {CallId}.", call.Id);
            }
        }
        await db.SaveChangesAsync(ct);
        return archived;
    }

    private bool TryValidateUrl(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || parsed.IsLoopback
            || Uri.CheckHostName(parsed.Host) is UriHostNameType.IPv4 or UriHostNameType.IPv6 or UriHostNameType.Unknown)
        {
            return false;
        }
        var allowed = _options.AllowedProviderHostSuffixes.Any(suffix =>
            parsed.Host.Equals(suffix.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
            || parsed.Host.EndsWith('.' + suffix.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
        if (!allowed) return false;
        uri = parsed;
        return true;
    }

    private static string NormalizeContentType(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128
            ? value.ToLowerInvariant()
            : "application/octet-stream";

    private static string BuildFileName(Uri uri, Guid callId, string contentType)
    {
        var candidate = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 240)
        {
            return candidate;
        }
        var extension = contentType switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/ogg" => ".ogg",
            "audio/wav" or "audio/x-wav" => ".wav",
            _ => ".bin"
        };
        return $"call-{callId:N}{extension}";
    }

    private static TimeSpan GetRetryDelay(int attempt) => attempt switch
    {
        <= 2 => TimeSpan.FromSeconds(30),
        <= 5 => TimeSpan.FromMinutes(2),
        <= 10 => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromHours(1)
    };
}

public sealed class ProviderRecordingArchiveHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ProviderRecordingArchiveHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ProviderRecordingArchiveService>()
                    .ProcessDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Provider recording archive synchronization failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

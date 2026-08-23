using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Orbita.TelephonyGateway;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>()
    ?? new GatewayOptions();
options.Validate();

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = options.MaxBodyBytes;
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EncryptedFileQueue>();
builder.Services.AddHttpClient<GatewayForwarder>(client =>
    {
        client.BaseAddress = options.GetOrbitaApiBaseUri();
        client.Timeout = TimeSpan.FromMinutes(5);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });
builder.Services.AddHostedService<QueuedDeliveryWorker>();
builder.Services.AddRateLimiter(rateLimiter =>
{
    rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiter.AddPolicy("telephony-webhook", context =>
    {
        var publicId = context.Request.RouteValues["publicId"]?.ToString() ?? "unknown";
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{publicId}:{remoteIp}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();
app.UseRateLimiter();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (EncryptedFileQueue queue, CancellationToken ct) =>
    await queue.CanWriteAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapGet(
        "/api/v1/integrations/telephony/asterisk/{publicId:guid}/route",
        async Task<IResult> (
            Guid publicId,
            HttpRequest request,
            GatewayForwarder forwarder,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var job = new GatewayWebhookJob(
                Guid.NewGuid(),
                "asterisk",
                publicId,
                HttpMethods.Get,
                request.QueryString.Value ?? string.Empty,
                null,
                ReadForwardedHeaders(request),
                string.Empty,
                timeProvider.GetUtcNow(),
                AttemptCount: 0,
                NextAttemptAtUtc: timeProvider.GetUtcNow());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var delivery = await forwarder.ForwardAsync(job, timeout.Token, "/route");
                return new ProxyResult(delivery);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
        })
    .RequireRateLimiting("telephony-webhook");

app.MapMethods(
        "/api/v1/integrations/telephony/{provider:regex(^(sipout|plusofon|asterisk)$)}/{publicId:guid}",
        [HttpMethods.Get, HttpMethods.Post],
        async Task<IResult> (
            string provider,
            Guid publicId,
            HttpRequest request,
            GatewayForwarder forwarder,
            EncryptedFileQueue queue,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            if (request.ContentLength is > 0 && request.ContentLength > options.MaxBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var body = await ReadBodyAsync(request, options.MaxBodyBytes, ct);
            if (body is null)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var now = timeProvider.GetUtcNow();
            var job = new GatewayWebhookJob(
                Guid.NewGuid(),
                provider.Trim().ToLowerInvariant(),
                publicId,
                request.Method,
                request.QueryString.Value ?? string.Empty,
                request.ContentType,
                ReadForwardedHeaders(request),
                body.Length == 0 ? string.Empty : Convert.ToBase64String(body),
                now,
                AttemptCount: 0,
                NextAttemptAtUtc: now);

            GatewayDeliveryResult delivery;
            try
            {
                delivery = await forwarder.ForwardAsync(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try
                {
                    await queue.EnqueueAsync(job, CancellationToken.None);
                    return Results.Accepted(value: new { status = "queued", jobId = job.Id });
                }
                catch (GatewayQueueFullException)
                {
                    return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
                }
            }

            if (delivery.IsSuccess)
            {
                return new ProxyResult(delivery);
            }

            if (!delivery.ShouldRetry)
            {
                return new ProxyResult(delivery);
            }

            try
            {
                await queue.EnqueueAsync(job, ct);
                return Results.Accepted(value: new { status = "queued", jobId = job.Id });
            }
            catch (GatewayQueueFullException)
            {
                return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
            }
        })
    .RequireRateLimiting("telephony-webhook");

app.Run();

static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
{
    if (request.ContentLength == 0)
    {
        return [];
    }

    using var destination = new MemoryStream();
    var buffer = new byte[8192];
    while (true)
    {
        var read = await request.Body.ReadAsync(buffer, ct);
        if (read == 0)
        {
            return destination.ToArray();
        }

        if (destination.Length + read > maxBytes)
        {
            return null;
        }

        await destination.WriteAsync(buffer.AsMemory(0, read), ct);
    }
}

static IReadOnlyDictionary<string, string> ReadForwardedHeaders(HttpRequest request)
{
    const string secretHeader = "X-Orbita-Webhook-Secret";
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (request.Headers.TryGetValue(secretHeader, out var secret) && !string.IsNullOrWhiteSpace(secret))
    {
        result[secretHeader] = secret.ToString();
    }
    return result;
}

public sealed class ProxyResult(GatewayDeliveryResult delivery) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = (int)delivery.StatusCode;
        if (!string.IsNullOrWhiteSpace(delivery.ContentType))
        {
            httpContext.Response.ContentType = delivery.ContentType;
        }

        httpContext.Response.ContentLength = delivery.Body.Length;
        await httpContext.Response.Body.WriteAsync(delivery.Body, httpContext.RequestAborted);
    }
}

public partial class Program;

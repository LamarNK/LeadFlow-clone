using System.Net;
using System.Security.Cryptography;
using System.Text;
using Orbita.Contracts;
using Orbita.TelephonyGateway;

namespace Orbita.Tests;

public sealed class TelephonyGatewayTests : IDisposable
{
    private readonly string queuePath = Path.Combine(
        Path.GetTempPath(),
        "orbita-telephony-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EncryptedQueue_RoundTripsJobWithoutLeavingSecretInPlainText()
    {
        var options = CreateOptions();
        var queue = new EncryptedFileQueue(options);
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var job = CreateJob(now, queryString: "?secret=do-not-store-in-plain-text&C_ID=call-42");

        await queue.EnqueueAsync(job);

        var path = Directory.GetFiles(queuePath, "*.job").Single();
        var raw = await File.ReadAllBytesAsync(path);
        Assert.DoesNotContain("do-not-store-in-plain-text", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("header-secret", Encoding.UTF8.GetString(raw));

        var due = await queue.GetDueAsync(now, limit: 10);
        var restored = Assert.Single(due);
        Assert.Equivalent(job, restored, strict: true);

        await queue.CompleteAsync(job.Id);
        Assert.Empty(Directory.GetFiles(queuePath, "*.job"));
    }

    [Fact]
    public async Task EncryptedQueue_ReschedulesAndDeadLettersJob()
    {
        var options = CreateOptions();
        var queue = new EncryptedFileQueue(options);
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var job = CreateJob(now);
        await queue.EnqueueAsync(job);

        var rescheduled = job with
        {
            AttemptCount = 3,
            NextAttemptAtUtc = now.AddMinutes(5)
        };
        await queue.RescheduleAsync(rescheduled);

        Assert.Empty(await queue.GetDueAsync(now, limit: 10));
        var due = Assert.Single(await queue.GetDueAsync(now.AddMinutes(6), limit: 10));
        Assert.Equal(3, due.AttemptCount);

        await queue.MoveToDeadLetterAsync(job.Id);
        Assert.Empty(Directory.GetFiles(queuePath, "*.job"));
        Assert.Single(Directory.GetFiles(queuePath, "*.dead"));
    }

    [Fact]
    public async Task Forwarder_PreservesWebhookPathQueryBodyAndContentType()
    {
        var handler = new CaptureHandler(HttpStatusCode.Accepted, "{\"status\":\"accepted\"}");
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://orbita-api:8080/")
        };
        var forwarder = new GatewayForwarder(client);
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var job = CreateJob(
            now,
            method: HttpMethod.Post.Method,
            queryString: "?secret=protected&C_ID=call-42",
            contentType: "application/json",
            body: "{\"duration\":42}");

        var result = await forwarder.ForwardAsync(job);

        Assert.True(result.IsSuccess);
        Assert.False(result.ShouldRetry);
        Assert.Equal(
            $"http://orbita-api:8080/api/v1/integrations/telephony/sipout/{job.PublicId:D}?secret=protected&C_ID=call-42",
            handler.RequestUri?.ToString());
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("application/json", handler.ContentType);
        Assert.Equal("{\"duration\":42}", handler.Body);
        Assert.Equal("header-secret", handler.WebhookSecret);
    }

    [Fact]
    public async Task Forwarder_RoutesPlusofonJobToPlusofonApi()
    {
        var handler = new CaptureHandler(HttpStatusCode.Accepted, string.Empty);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://orbita-api:8080/")
        };
        var forwarder = new GatewayForwarder(client);
        var job = CreateJob(
            DateTimeOffset.Parse("2026-08-17T12:00:00Z"),
            method: HttpMethod.Post.Method,
            queryString: string.Empty,
            provider: CrmTelephonyProviders.Plusofon);

        var result = await forwarder.ForwardAsync(job);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            $"http://orbita-api:8080/api/v1/integrations/telephony/plusofon/{job.PublicId:D}",
            handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task Forwarder_PreservesAsteriskMultipartBodyAndSecret()
    {
        var handler = new CaptureHandler(HttpStatusCode.Accepted, string.Empty);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://orbita-api:8080/")
        };
        var forwarder = new GatewayForwarder(client);
        var boundary = "orbita-test-boundary";
        var body = $"--{boundary}\r\nContent-Disposition: form-data; name=\"external_call_id\"\r\n\r\ncall-1\r\n--{boundary}--\r\n";
        var job = CreateJob(
            DateTimeOffset.Parse("2026-08-18T12:00:00Z"),
            method: HttpMethod.Post.Method,
            queryString: string.Empty,
            contentType: $"multipart/form-data; boundary={boundary}",
            body: body,
            provider: CrmTelephonyProviders.Asterisk);

        var result = await forwarder.ForwardAsync(job);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            $"http://orbita-api:8080/api/v1/integrations/telephony/asterisk/{job.PublicId:D}",
            handler.RequestUri?.ToString());
        Assert.Equal(body, handler.Body);
        Assert.Equal("header-secret", handler.WebhookSecret);
    }

    [Fact]
    public async Task Forwarder_RoutesAsteriskCallbackLookupWithoutChangingQueryOrSecret()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, "201^PJSIP/202-webrtc");
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://orbita-api:8080/")
        };
        var forwarder = new GatewayForwarder(client);
        var job = CreateJob(
            DateTimeOffset.Parse("2026-08-18T12:00:00Z"),
            method: HttpMethod.Get.Method,
            queryString: "?caller=79991112233&called=74950000000",
            provider: CrmTelephonyProviders.Asterisk);

        var result = await forwarder.ForwardAsync(job, pathSuffix: "/route");

        Assert.True(result.IsSuccess);
        Assert.Equal(
            $"http://orbita-api:8080/api/v1/integrations/telephony/asterisk/{job.PublicId:D}/route?caller=79991112233&called=74950000000",
            handler.RequestUri?.ToString());
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("header-secret", handler.WebhookSecret);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    public async Task Forwarder_ClassifiesRetryableStatuses(HttpStatusCode statusCode, bool expectedRetry)
    {
        var handler = new CaptureHandler(statusCode, string.Empty);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://orbita-api:8080/")
        };
        var forwarder = new GatewayForwarder(client);

        var result = await forwarder.ForwardAsync(CreateJob(DateTimeOffset.UtcNow));

        Assert.Equal(expectedRetry, result.ShouldRetry);
    }

    private GatewayOptions CreateOptions() => new()
    {
        OrbitaApiBaseUrl = "http://orbita-api:8080",
        QueuePath = queuePath,
        QueueEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        MaxQueueItems = 100,
        MaxDeliveryAttempts = 5
    };

    private static GatewayWebhookJob CreateJob(
        DateTimeOffset now,
        string method = "GET",
        string queryString = "?secret=protected&C_ID=call-42",
        string? contentType = null,
        string body = "",
        string provider = CrmTelephonyProviders.Sipout) => new(
            Guid.NewGuid(),
            provider,
            Guid.NewGuid(),
            method,
            queryString,
            contentType,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Orbita-Webhook-Secret"] = "header-secret"
            },
            string.IsNullOrEmpty(body) ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(body)),
            now,
            AttemptCount: 0,
            NextAttemptAtUtc: now);

    public void Dispose()
    {
        if (Directory.Exists(queuePath))
        {
            Directory.Delete(queuePath, recursive: true);
        }
    }

    private sealed class CaptureHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? ContentType { get; private set; }
        public string? Body { get; private set; }
        public string? WebhookSecret { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            WebhookSecret = request.Headers.TryGetValues("X-Orbita-Webhook-Secret", out var values)
                ? values.Single()
                : null;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}

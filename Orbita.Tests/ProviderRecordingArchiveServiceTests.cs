using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class ProviderRecordingArchiveServiceTests
{
    [Fact]
    public async Task ProcessDueAsync_CopiesProviderRecordingIntoPrivateStorage()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "orbita-provider-recording-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
            var dbOptions = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase($"provider-recording-{Guid.NewGuid():N}")
                .Options;
            await using var db = new OrbitaDbContext(dbOptions);
            var callId = Guid.NewGuid();
            db.CrmCalls.Add(new CrmCallEntity
            {
                Id = callId,
                OfficeId = Guid.NewGuid(),
                Provider = CrmTelephonyProviders.Plusofon,
                ExternalCallId = "call-with-recording",
                Direction = CrmCallDirections.Outgoing,
                ClientPhoneNormalized = "79991112233",
                StartedAtUtc = now.UtcDateTime,
                ReceivedAtUtc = now.UtcDateTime,
                UpdatedAtUtc = now.UtcDateTime,
                RecordingUrl = "https://rec.plusofon.ru/recording.mp3",
                NextRecordingArchiveAtUtc = now.UtcDateTime
            });
            await db.SaveChangesAsync();
            var recordingOptions = Options.Create(new CrmCallRecordingOptions
            {
                DataPath = storagePath,
                MaxUploadBytes = 1024 * 1024,
                AllowedProviderHostSuffixes = ["plusofon.ru"]
            });
            var sut = new ProviderRecordingArchiveService(
                db,
                new StubHttpClientFactory([1, 2, 3, 4]),
                new CrmCallRecordingStorageService(recordingOptions),
                recordingOptions,
                new FixedTimeProvider(now),
                NullLogger<ProviderRecordingArchiveService>.Instance);

            var archived = await sut.ProcessDueAsync();

            Assert.Equal(1, archived);
            var call = await db.CrmCalls.SingleAsync();
            Assert.NotNull(call.RecordingStoragePath);
            Assert.Equal("recording.mp3", call.RecordingFileName);
            Assert.Equal("audio/mpeg", call.RecordingContentType);
            Assert.Null(call.NextRecordingArchiveAtUtc);
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(Path.Combine(storagePath, call.RecordingStoragePath!)));
        }
        finally
        {
            if (Directory.Exists(storagePath)) Directory.Delete(storagePath, recursive: true);
        }
    }

    private sealed class StubHttpClientFactory(byte[] content) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(content));
    }

    private sealed class StubHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.ContentType = new("audio/mpeg");
            return Task.FromResult(response);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

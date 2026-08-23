using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class PlusofonRecordingSyncServiceTests
{
    [Fact]
    public async Task ProcessDueAsync_AttachesReadyRecordingAndClearsRetry()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase($"plusofon-recording-{Guid.NewGuid():N}")
            .Options;
        await using var db = new OrbitaDbContext(options);
        var officeId = Guid.NewGuid();
        var callId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 17, 18, 0, 0, TimeSpan.Zero);
        var protector = new CrmTelephonyCredentialProtector(new EphemeralDataProtectionProvider());
        db.CrmTelephonyWebhooks.Add(new CrmTelephonyWebhookEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            Provider = CrmTelephonyProviders.Plusofon,
            PublicId = Guid.NewGuid(),
            SecretHash = "hash",
            IsEnabled = true,
            ProviderClientId = "client-1",
            ProviderAccessTokenProtected = protector.Protect("access-token"),
            CreatedAtUtc = now.UtcDateTime,
            UpdatedAtUtc = now.UtcDateTime
        });
        db.CrmCalls.Add(new CrmCallEntity
        {
            Id = callId,
            OfficeId = officeId,
            Provider = CrmTelephonyProviders.Plusofon,
            ExternalCallId = "provider-call-1",
            Direction = CrmCallDirections.Outgoing,
            ClientPhoneNormalized = "79991112233",
            StartedAtUtc = now.UtcDateTime,
            ReceivedAtUtc = now.UtcDateTime,
            UpdatedAtUtc = now.UtcDateTime,
            NextRecordingFetchAtUtc = now.UtcDateTime
        });
        await db.SaveChangesAsync();
        var api = new StubPlusofonApiClient();
        var sut = new PlusofonRecordingSyncService(
            db,
            protector,
            api,
            new FixedTimeProvider(now),
            NullLogger<PlusofonRecordingSyncService>.Instance);

        var updated = await sut.ProcessDueAsync();

        Assert.Equal(1, updated);
        var call = await db.CrmCalls.SingleAsync(x => x.Id == callId);
        Assert.Equal("https://records.plusofon.test/provider-call-1.mp3", call.RecordingUrl);
        Assert.Null(call.NextRecordingFetchAtUtc);
        Assert.Equal(1, call.RecordingFetchAttempts);
        Assert.Equal("client-1", api.ClientId);
        Assert.Equal("access-token", api.AccessToken);
        Assert.Equal("provider-call-1", api.CallId);
    }

    private sealed class StubPlusofonApiClient : IPlusofonApiClient
    {
        public string? ClientId { get; private set; }
        public string? AccessToken { get; private set; }
        public string? CallId { get; private set; }

        public Task<PlusofonRecordingResult> GetRecordingAsync(
            string clientId,
            string accessToken,
            string callId,
            CancellationToken ct = default)
        {
            ClientId = clientId;
            AccessToken = accessToken;
            CallId = callId;
            return Task.FromResult(new PlusofonRecordingResult(
                PlusofonRecordingOutcome.Ready,
                $"https://records.plusofon.test/{callId}.mp3"));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

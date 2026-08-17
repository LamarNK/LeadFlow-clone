using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmTelephonyServiceTests
{
    [Fact]
    public async Task SipoutCall_MatchesCardAndManager_AndStoresRecording()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync();

        var result = await harness.Sut.ReceiveSipoutCallAsync(
            receiver.PublicId,
            receiver.Secret,
            new SipoutCallWebhookPayload(
                "call-100",
                "201",
                "+7 (999) 111-22-33",
                "outgoing",
                "201",
                null,
                "1786968000",
                "65",
                "https://records.sipout.test/call-100.mp3"));

        Assert.Equal(SipoutCallReceiveOutcome.Accepted, result.Outcome);
        Assert.Equal(harness.CardId, result.CardId);
        var call = Assert.Single(await harness.Db.CrmCalls.ToListAsync());
        Assert.Equal(harness.CardId, call.CardId);
        Assert.Equal(Harness.ManagerId, call.ManagerUserId);
        Assert.Equal("201", call.ProviderUserKey);
        Assert.Equal(CrmCallDirections.Outgoing, call.Direction);
        Assert.Equal("79991112233", call.ClientPhoneNormalized);
        Assert.Equal(65, call.DurationSeconds);
        Assert.Equal("https://records.sipout.test/call-100.mp3", call.RecordingUrl);
    }

    [Fact]
    public async Task SipoutCall_WithSameExternalId_UpdatesInsteadOfDuplicating()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync();
        var first = new SipoutCallWebhookPayload(
            "same-call", "201", "89991112233", "outgoing", "201", null, null, "2", null);

        var created = await harness.Sut.ReceiveSipoutCallAsync(receiver.PublicId, receiver.Secret, first);
        var updated = await harness.Sut.ReceiveSipoutCallAsync(
            receiver.PublicId,
            receiver.Secret,
            first with { DurationSeconds = "91", RecordingUrl = "https://records.sipout.test/same-call.mp3" });

        Assert.Equal(SipoutCallReceiveOutcome.Accepted, created.Outcome);
        Assert.Equal(SipoutCallReceiveOutcome.Updated, updated.Outcome);
        var call = Assert.Single(await harness.Db.CrmCalls.ToListAsync());
        Assert.Equal(91, call.DurationSeconds);
        Assert.Equal("https://records.sipout.test/same-call.mp3", call.RecordingUrl);
    }

    [Fact]
    public async Task SipoutCall_MatchesAdditionalCandidatePhone()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync();
        harness.Db.CandidateContactPhones.Add(new CandidateContactPhoneEntity
        {
            Id = Guid.NewGuid(),
            PersonId = harness.PersonId,
            PhoneRaw = "+7 912 555-44-33",
            PhoneNormalized = "79125554433",
            CreatedAtUtc = harness.Now.UtcDateTime
        });
        await harness.Db.SaveChangesAsync();

        var result = await harness.Sut.ReceiveSipoutCallAsync(
            receiver.PublicId,
            receiver.Secret,
            new SipoutCallWebhookPayload(
                "additional-phone", "+7 912 555-44-33", "201", "incoming", "201", null, null, "30", null));

        Assert.Equal(SipoutCallReceiveOutcome.Accepted, result.Outcome);
        Assert.Equal(harness.CardId, result.CardId);
    }

    [Fact]
    public async Task SipoutCall_WithInvalidSecret_IsRejectedWithoutWriting()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync();

        var result = await harness.Sut.ReceiveSipoutCallAsync(
            receiver.PublicId,
            "wrong-secret",
            new SipoutCallWebhookPayload(
                "forged-call", "201", "89991112233", "outgoing", "201", null, null, "10", null));

        Assert.Equal(SipoutCallReceiveOutcome.Unauthorized, result.Outcome);
        Assert.Empty(await harness.Db.CrmCalls.ToListAsync());
    }

    [Fact]
    public async Task SipoutCall_WithoutKnownPhone_IsStoredAsUnmatched()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync();

        var result = await harness.Sut.ReceiveSipoutCallAsync(
            receiver.PublicId,
            receiver.Secret,
            new SipoutCallWebhookPayload(
                "unknown-client", "201", "+7 900 000-00-01", "outgoing", "201", null, null, "10", null));

        Assert.Equal(SipoutCallReceiveOutcome.Unmatched, result.Outcome);
        Assert.Null(Assert.Single(await harness.Db.CrmCalls.ToListAsync()).CardId);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public const string ManagerId = "sipout-manager";
        public DateTimeOffset Now { get; } = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
        public OrbitaDbContext Db { get; }
        public CrmTelephonyService Sut { get; }
        public Guid OfficeId { get; }
        public Guid PersonId { get; }
        public Guid CardId { get; }

        private Harness(OrbitaDbContext db, Guid officeId, Guid personId, Guid cardId)
        {
            Db = db;
            OfficeId = officeId;
            PersonId = personId;
            CardId = cardId;
            Sut = new CrmTelephonyService(db, new PhoneNormalizer(), new FixedTimeProvider(Now));
        }

        public static async Task<Harness> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase($"crm-telephony-{Guid.NewGuid():N}")
                .Options;
            var db = new OrbitaDbContext(options);
            var officeId = Guid.NewGuid();
            var personId = Guid.NewGuid();
            var responseId = Guid.NewGuid();
            var cardId = Guid.NewGuid();
            var now = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
            db.Offices.Add(new OfficeEntity
            {
                Id = officeId,
                Name = "SIPOUT office",
                RegistrationSecretHash = "hash",
                IsEnabled = true,
                CrmEnabled = true,
                CreatedAtUtc = now
            });
            db.Users.Add(new IdentityUser
            {
                Id = ManagerId,
                UserName = "sipout-manager@orbita.local",
                NormalizedUserName = "SIPOUT-MANAGER@ORBITA.LOCAL",
                Email = "sipout-manager@orbita.local",
                NormalizedEmail = "SIPOUT-MANAGER@ORBITA.LOCAL"
            });
            db.PanelUserProfiles.Add(new PanelUserProfileEntity
            {
                UserId = ManagerId,
                OfficeId = officeId,
                FullName = "Менеджер SIPOUT"
            });
            var person = TestCandidatePersonFactory.CreatePerson(
                officeId,
                phoneRaw: "+7 999 111-22-33",
                phoneNormalized: "79991112233",
                createdAtUtc: now);
            person.Id = personId;
            db.CandidatePersons.Add(person);
            db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(
                officeId,
                personId,
                phone: "79991112233",
                id: responseId,
                createdAt: now));
            db.CrmCandidateCards.Add(new CrmCandidateCardEntity
            {
                Id = cardId,
                OfficeId = officeId,
                ResponseId = responseId,
                Stage = CrmStages.Lead,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                StageChangedAtUtc = now
            });
            await db.SaveChangesAsync();
            return new Harness(db, officeId, personId, cardId);
        }

        public async Task<(Guid PublicId, string Secret)> CreateReceiverAsync()
        {
            var (receiver, error) = await Sut.RotateReceiverAsync(OfficeId, "https://orbita.test");
            Assert.Null(error);
            Assert.NotNull(receiver);
            var marker = "?secret=";
            var start = receiver.SipoutWebRequestUrl.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = receiver.SipoutWebRequestUrl.IndexOf('&', start);
            var secret = Uri.UnescapeDataString(receiver.SipoutWebRequestUrl[start..end]);
            var (binding, bindingError) = await Sut.SetBindingAsync(OfficeId, ManagerId, "201");
            Assert.Null(bindingError);
            Assert.NotNull(binding);
            return (receiver.PublicId, secret);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixWorkforceEventReceiverTests
{
    [Fact]
    public async Task ReceiveAsync_ValidatesAndDeduplicatesEvent()
    {
        await using var db = CreateDb();
        var publicId = await SeedAsync(db);
        var sut = new BitrixWorkforceEventReceiver(db);
        var incoming = NewIncoming();

        var first = await sut.ReceiveAsync(publicId, incoming);
        var second = await sut.ReceiveAsync(publicId, incoming);

        Assert.Equal(BitrixWorkforceReceiveOutcome.Accepted, first.Outcome);
        Assert.Equal(BitrixWorkforceReceiveOutcome.Duplicate, second.Outcome);
        Assert.Single(db.BitrixDealEventInbox);
        Assert.Equal(1001, db.BitrixDealEventInbox.Single().DealId);
    }

    [Fact]
    public async Task ReceiveAsync_RejectsWrongTokenWithoutPersistingPayload()
    {
        await using var db = CreateDb();
        var publicId = await SeedAsync(db);
        var sut = new BitrixWorkforceEventReceiver(db);
        var incoming = NewIncoming() with { ApplicationToken = "wrong-token-value" };

        var result = await sut.ReceiveAsync(publicId, incoming);

        Assert.Equal(BitrixWorkforceReceiveOutcome.Unauthorized, result.Outcome);
        Assert.Empty(db.BitrixDealEventInbox);
    }

    [Fact]
    public async Task Parser_ReadsOfficialBitrixFormShape()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>
            {
                ["event"] = "ONCRMDEALUPDATE",
                ["event_handler_id"] = "201",
                ["data[FIELDS][ID]"] = "759",
                ["ts"] = "1736405807",
                ["auth[domain]"] = "example.bitrix24.ru",
                ["auth[member_id]"] = "member-1",
                ["auth[application_token]"] = "1234567890abcdef"
            });

        var parsed = await BitrixWorkforceEventParser.ParseAsync(
            context.Request,
            CancellationToken.None);

        Assert.NotNull(parsed);
        Assert.Equal("ONCRMDEALUPDATE", parsed.EventName);
        Assert.Equal(759, parsed.DealId);
        Assert.Equal("example.bitrix24.ru", parsed.Domain);
        Assert.Equal("1234567890abcdef", parsed.ApplicationToken);
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new OrbitaDbContext(options);
    }

    private static async Task<Guid> SeedAsync(OrbitaDbContext db)
    {
        var officeId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var publicId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.BitrixInstances.Add(new BitrixInstanceEntity
        {
            Id = instanceId,
            OfficeId = officeId,
            Name = "Bitrix",
            PortalHost = "example.bitrix24.ru",
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.BitrixWorkforceConfigurations.Add(new BitrixWorkforceConfigurationEntity
        {
            BitrixInstanceId = instanceId,
            OperationMode = BitrixWorkforceDistribution.ShadowMode,
            TimeZoneId = "Europe/Moscow",
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.BitrixWorkforceEventCredentials.Add(new BitrixWorkforceEventCredentialEntity
        {
            BitrixInstanceId = instanceId,
            PublicId = publicId,
            ApplicationTokenHash = BitrixWorkforceSettingsService.HashToken("1234567890abcdef"),
            ExpectedMemberId = "member-1",
            ConfiguredAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return publicId;
    }

    private static BitrixWorkforceIncomingEvent NewIncoming() => new(
        "ONCRMDEALUPDATE",
        "201",
        1001,
        1736405807,
        "example.bitrix24.ru",
        "member-1",
        "1234567890abcdef");
}

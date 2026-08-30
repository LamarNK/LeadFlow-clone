using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmTelephonyProviderAccountServiceTests
{
    [Fact]
    public async Task CreatePlusofonAccount_ReturnsPasteableWebhookAndProtectsToken()
    {
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase($"telephony-provider-account-{Guid.NewGuid():N}")
            .Options;
        await using var db = new OrbitaDbContext(options);
        var officeId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Provider account office",
            RegistrationSecretHash = "hash",
            IsEnabled = true,
            CrmEnabled = true,
            CreatedAtUtc = now.UtcDateTime
        });
        await db.SaveChangesAsync();
        var protector = new CrmTelephonyCredentialProtector(new EphemeralDataProtectionProvider());
        var sut = new CrmTelephonyProviderAccountService(
            db,
            new PhoneNormalizer(),
            protector,
            new FixedTimeProvider(now));

        var (result, error) = await sut.CreateAsync(
            officeId,
            CrmTelephonyProviders.Plusofon,
            new CreateCrmTelephonyProviderAccountRequest(
                "Основной кабинет",
                "client-3017216",
                "very-secret-token",
                ["+7 (999) 111-22-33", "79991112233"]),
            "https://orbita.example");

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Contains("/plusofon/", result.CallbackUrl, StringComparison.Ordinal);
        Assert.Contains("?secret=", result.CallbackUrl, StringComparison.Ordinal);
        Assert.Equal("X-Orbita-Webhook-Secret", result.WebhookSecretHeader);
        Assert.Single(result.Account.OwnedNumbers);
        Assert.Equal("79991112233", result.Account.OwnedNumbers[0]);
        Assert.Equal(now.UtcDateTime.AddDays(-7), result.Account.SyncFromUtc);

        var stored = await db.CrmTelephonyProviderAccounts.SingleAsync();
        Assert.NotEqual("very-secret-token", stored.AccessTokenProtected);
        Assert.Equal("very-secret-token", protector.Unprotect(stored.AccessTokenProtected!));
    }

    [Fact]
    public async Task CreatePlusofonAccount_RejectsSameCabinetTwiceInOffice()
    {
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase($"telephony-provider-duplicate-{Guid.NewGuid():N}")
            .Options;
        await using var db = new OrbitaDbContext(options);
        var officeId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Provider duplicate office",
            RegistrationSecretHash = "hash",
            IsEnabled = true,
            CrmEnabled = true,
            CreatedAtUtc = now.UtcDateTime
        });
        await db.SaveChangesAsync();
        var sut = new CrmTelephonyProviderAccountService(
            db,
            new PhoneNormalizer(),
            new CrmTelephonyCredentialProtector(new EphemeralDataProtectionProvider()),
            new FixedTimeProvider(now));
        var first = await sut.CreateAsync(
            officeId,
            CrmTelephonyProviders.Plusofon,
            new CreateCrmTelephonyProviderAccountRequest("Первый", "same-client", "token", []),
            "https://orbita.example");

        var second = await sut.CreateAsync(
            officeId,
            CrmTelephonyProviders.Plusofon,
            new CreateCrmTelephonyProviderAccountRequest("Второй", "same-client", "other-token", []),
            "https://orbita.example");

        Assert.NotNull(first.Result);
        Assert.Null(second.Result);
        Assert.Equal("Этот кабинет провайдера уже подключён к офису.", second.Error);
    }

    [Fact]
    public async Task DeleteAccount_RemovesSettingsButPreservesImportedCallsAndRecordings()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase($"telephony-provider-delete-{Guid.NewGuid():N}")
            .Options;
        await using var db = new OrbitaDbContext(options);
        var officeId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Provider delete office",
            RegistrationSecretHash = "hash",
            IsEnabled = true,
            CrmEnabled = true,
            CreatedAtUtc = now.UtcDateTime
        });
        db.CrmTelephonyProviderAccounts.Add(new CrmTelephonyProviderAccountEntity
        {
            Id = accountId,
            OfficeId = officeId,
            Provider = CrmTelephonyProviders.Sipout,
            Name = "SIPOUT office",
            OwnedNumbersJson = "[]",
            PublicId = Guid.NewGuid(),
            SecretHash = "hash",
            IsEnabled = true,
            SyncFromUtc = now.UtcDateTime.AddDays(-7),
            SyncStatus = "online",
            CreatedAtUtc = now.UtcDateTime,
            UpdatedAtUtc = now.UtcDateTime
        });
        db.CrmTelephonyProviderAccountBindings.Add(new CrmTelephonyProviderAccountBindingEntity
        {
            Id = Guid.NewGuid(),
            ProviderAccountId = accountId,
            ProviderUserKey = "202",
            UserId = "manager-1",
            CreatedAtUtc = now.UtcDateTime,
            UpdatedAtUtc = now.UtcDateTime
        });
        db.CrmCalls.Add(new CrmCallEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            Provider = CrmTelephonyProviders.Sipout,
            ProviderAccountId = accountId,
            ExternalCallId = "sipout-call-1",
            Direction = CrmCallDirections.Outgoing,
            CallerPhone = "202",
            CalledPhone = "79991112233",
            ClientPhoneNormalized = "79991112233",
            StartedAtUtc = now.UtcDateTime,
            RecordingStoragePath = "calls/recording.mp3",
            ReceivedAtUtc = now.UtcDateTime,
            UpdatedAtUtc = now.UtcDateTime
        });
        await db.SaveChangesAsync();
        var sut = new CrmTelephonyProviderAccountService(
            db,
            new PhoneNormalizer(),
            new CrmTelephonyCredentialProtector(new EphemeralDataProtectionProvider()),
            new FixedTimeProvider(now));

        var (success, error) = await sut.DeleteAsync(
            officeId,
            CrmTelephonyProviders.Sipout,
            accountId);

        Assert.True(success);
        Assert.Null(error);
        Assert.Empty(await db.CrmTelephonyProviderAccounts.ToListAsync());
        Assert.Empty(await db.CrmTelephonyProviderAccountBindings.ToListAsync());
        var call = await db.CrmCalls.SingleAsync();
        Assert.Null(call.ProviderAccountId);
        Assert.StartsWith($"removed:{accountId:N}:", call.ExternalCallId, StringComparison.Ordinal);
        Assert.Equal("calls/recording.mp3", call.RecordingStoragePath);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

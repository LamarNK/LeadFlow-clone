using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmTelephonyServiceTests
{
    [Fact]
    public async Task BeelineSipAccount_IsEncrypted_AndWrittenToRuntimeWithoutLosingPassword()
    {
        var runtimePath = Path.Combine(Path.GetTempPath(), "orbita-sip-runtime-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase($"crm-telephony-beeline-{Guid.NewGuid():N}")
                .Options;
            await using var db = new OrbitaDbContext(options);
            var officeId = Guid.NewGuid();
            db.Offices.Add(new OfficeEntity
            {
                Id = officeId,
                Name = "Beeline office",
                RegistrationSecretHash = "hash",
                IsEnabled = true,
                CrmEnabled = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var protector = new CrmTelephonyCredentialProtector(new EphemeralDataProtectionProvider());
            var writer = new CrmSipRuntimeConfigWriter(Options.Create(new CrmSipRuntimeOptions
            {
                ConfigPath = runtimePath
            }));
            var sut = new CrmTelephonyService(
                db,
                new PhoneNormalizer(),
                TimeProvider.System,
                credentialProtector: protector,
                sipRuntimeConfigWriter: writer);

            var first = await sut.SetBeelineSipAccountAsync(
                officeId,
                new UpdateSipProviderAccountRequest(
                    "sip.beeline.test",
                    "beeline.test",
                    5060,
                    "udp",
                    "sip-user",
                    "auth-user@beeline.test",
                    "test-secret",
                    true));
            Assert.True(first.Success, first.Error);

            var stored = Assert.Single(await db.CrmTelephonyWebhooks.ToListAsync());
            Assert.DoesNotContain("test-secret", stored.ProviderAccessTokenProtected, StringComparison.Ordinal);
            var runtimeConfig = await File.ReadAllTextAsync(Path.Combine(runtimePath, $"beeline.{officeId:D}.conf"));
            var endpointKey = officeId.ToString("N");
            Assert.Contains($"[beeline-{endpointKey}]", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains($"[beeline-{endpointKey}-registration]", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains($"set_var=ORBITA_OFFICE_ID={officeId:D}", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("username=auth-user@beeline.test", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("password=test-secret", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("contact=sip:beeline.test:5060", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("server_uri=sip:beeline.test:5060", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("client_uri=sip:sip-user@beeline.test", runtimeConfig, StringComparison.Ordinal);
            Assert.Equal(
                2,
                runtimeConfig.Split("outbound_proxy=sip:sip.beeline.test:5060\\;lr", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("server_uri=sip:sip.beeline.test:5060", runtimeConfig, StringComparison.Ordinal);
            Assert.Equal("beeline\n", await File.ReadAllTextAsync(Path.Combine(runtimePath, $"beeline.{officeId:D}.outbound")));

            var update = await sut.SetBeelineSipAccountAsync(
                officeId,
                new UpdateSipProviderAccountRequest(
                    "new-sip.beeline.test",
                    "beeline.test",
                    5060,
                    "udp",
                    "sip-user",
                    "auth-user@beeline.test",
                    string.Empty,
                    false));
            Assert.True(update.Success, update.Error);
            runtimeConfig = await File.ReadAllTextAsync(Path.Combine(runtimePath, $"beeline.{officeId:D}.conf"));
            Assert.Contains("password=test-secret", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("server_uri=sip:beeline.test:5060", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("outbound_proxy=sip:new-sip.beeline.test:5060\\;lr", runtimeConfig, StringComparison.Ordinal);
            Assert.Equal("primary\n", await File.ReadAllTextAsync(Path.Combine(runtimePath, $"beeline.{officeId:D}.outbound")));

            var settings = await sut.GetSettingsAsync(officeId, provider: CrmTelephonyProviders.Beeline);
            Assert.NotNull(settings?.SipAccount);
            Assert.True(settings.SipAccount.PasswordConfigured);
            Assert.Equal("new-sip.beeline.test", settings.SipAccount.Server);
        }
        finally
        {
            if (Directory.Exists(runtimePath))
            {
                Directory.Delete(runtimePath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AsteriskBinding_StoresPerEmployeeOutboundProvider()
    {
        await using var harness = await Harness.CreateAsync();

        var (binding, error) = await harness.Sut.SetBindingAsync(
            harness.OfficeId,
            Harness.ManagerId,
            "201",
            provider: CrmTelephonyProviders.Asterisk,
            outboundProvider: CrmTelephonyProviders.Beeline);

        Assert.Null(error);
        Assert.NotNull(binding);
        Assert.Equal(CrmTelephonyProviders.Beeline, binding.OutboundProvider);

        var stored = Assert.Single(await harness.Db.CrmTelephonyUserBindings.ToListAsync());
        Assert.Equal(CrmTelephonyProviders.Asterisk, stored.Provider);
        Assert.Equal("201", stored.ProviderUserKey);
        Assert.Equal(CrmTelephonyProviders.Beeline, stored.OutboundProvider);
    }

    [Fact]
    public async Task AsteriskBinding_RejectsUnknownOutboundProvider()
    {
        await using var harness = await Harness.CreateAsync();

        var (binding, error) = await harness.Sut.SetBindingAsync(
            harness.OfficeId,
            Harness.ManagerId,
            "201",
            provider: CrmTelephonyProviders.Asterisk,
            outboundProvider: "unknown-provider");

        Assert.Null(binding);
        Assert.Equal("Выбрана неизвестная линия для исходящих звонков.", error);
        Assert.Empty(await harness.Db.CrmTelephonyUserBindings.ToListAsync());
    }

    [Fact]
    public async Task AsteriskBinding_RejectsDuplicateExtensionFromAnotherOffice()
    {
        await using var harness = await Harness.CreateAsync();
        var (first, firstError) = await harness.Sut.SetBindingAsync(
            harness.OfficeId,
            Harness.ManagerId,
            "201",
            provider: CrmTelephonyProviders.Asterisk);
        Assert.Null(firstError);
        Assert.NotNull(first);

        var otherOfficeId = Guid.NewGuid();
        const string otherManagerId = "other-office-manager";
        harness.Db.Offices.Add(new OfficeEntity
        {
            Id = otherOfficeId,
            Name = "Other office",
            RegistrationSecretHash = "hash",
            IsEnabled = true,
            CrmEnabled = true,
            CreatedAtUtc = harness.Now.UtcDateTime
        });
        harness.Db.Users.Add(new IdentityUser
        {
            Id = otherManagerId,
            UserName = "other-office-manager@orbita.local",
            NormalizedUserName = "OTHER-OFFICE-MANAGER@ORBITA.LOCAL",
            Email = "other-office-manager@orbita.local",
            NormalizedEmail = "OTHER-OFFICE-MANAGER@ORBITA.LOCAL"
        });
        harness.Db.PanelUserProfiles.Add(new PanelUserProfileEntity
        {
            UserId = otherManagerId,
            OfficeId = otherOfficeId,
            FullName = "Другой менеджер"
        });
        await harness.Db.SaveChangesAsync();

        var (duplicate, duplicateError) = await harness.Sut.SetBindingAsync(
            otherOfficeId,
            otherManagerId,
            "201",
            provider: CrmTelephonyProviders.Asterisk);

        Assert.Null(duplicate);
        Assert.NotNull(duplicateError);
        Assert.Contains("другого офиса", duplicateError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SipRuntimeWriter_KeepsFourOfficeConfigsAndReceiversIsolated()
    {
        var runtimePath = Path.Combine(Path.GetTempPath(), "orbita-sip-runtime-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new CrmSipRuntimeConfigWriter(Options.Create(new CrmSipRuntimeOptions
            {
                ConfigPath = runtimePath
            }));
            var officeIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            foreach (var (officeId, index) in officeIds.Select((officeId, index) => (officeId, index)))
            {
                var result = await writer.WriteBeelineAsync(
                    officeId,
                    new CrmSipRuntimeAccount(
                        $"proxy-{index}.beeline.test",
                        "beeline.test",
                        5060,
                        "udp",
                        $"sip-{index}",
                        $"auth-{index}",
                        $"secret-{index}",
                        index == 0));
                Assert.True(result.Success, result.Error);
                var receiver = await writer.WriteAsteriskReceiverAsync(
                    officeId,
                    new CrmAsteriskRuntimeReceiver(Guid.NewGuid(), $"webhook-secret-{index}"));
                Assert.True(receiver.Success, receiver.Error);
            }

            Assert.Equal(4, Directory.GetFiles(runtimePath, "beeline.*.conf").Length);
            Assert.Equal(4, Directory.GetFiles(runtimePath, "receiver.*.conf").Length);
            foreach (var officeId in officeIds)
            {
                var config = await File.ReadAllTextAsync(Path.Combine(runtimePath, $"beeline.{officeId:D}.conf"));
                Assert.Contains($"[beeline-{officeId:N}]", config, StringComparison.Ordinal);
                Assert.Contains($"set_var=ORBITA_OFFICE_ID={officeId:D}", config, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(runtimePath))
            {
                Directory.Delete(runtimePath, recursive: true);
            }
        }
    }

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

    [Fact]
    public async Task PlusofonCall_MatchesCardAndInternalNumber_WithoutMixingProviders()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync(CrmTelephonyProviders.Plusofon);

        var result = await harness.Sut.ReceivePlusofonCallAsync(
            receiver.PublicId,
            receiver.Secret,
            new PlusofonCallWebhookPayload(
                "plusofon-call-100",
                "201",
                "+7 (999) 111-22-33",
                "outbound",
                "201",
                "1786968000",
                "42",
                "https://records.plusofon.test/plusofon-call-100.mp3"));

        Assert.Equal(SipoutCallReceiveOutcome.Accepted, result.Outcome);
        Assert.Equal(harness.CardId, result.CardId);
        var call = Assert.Single(await harness.Db.CrmCalls.ToListAsync());
        Assert.Equal(CrmTelephonyProviders.Plusofon, call.Provider);
        Assert.Equal(Harness.ManagerId, call.ManagerUserId);
        Assert.Equal("201", call.ProviderUserKey);
        Assert.Equal("79991112233", call.ClientPhoneNormalized);
        Assert.Equal(42, call.DurationSeconds);
    }

    [Fact]
    public async Task PlusofonCall_WithoutInlineRecording_SchedulesRecordingFetch()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync(CrmTelephonyProviders.Plusofon);

        var result = await harness.Sut.ReceivePlusofonCallAsync(
            receiver.PublicId,
            receiver.Secret,
            new PlusofonCallWebhookPayload(
                "plusofon-record-later",
                "201",
                "+7 (999) 111-22-33",
                "outbound",
                "201",
                "1786968000",
                "42",
                null));

        Assert.Equal(SipoutCallReceiveOutcome.Accepted, result.Outcome);
        var call = Assert.Single(await harness.Db.CrmCalls.ToListAsync());
        Assert.Null(call.RecordingUrl);
        Assert.Equal(harness.Now.UtcDateTime, call.NextRecordingFetchAtUtc);
        Assert.Equal(0, call.RecordingFetchAttempts);
    }

    [Fact]
    public async Task AsteriskCall_StoresPrivateRecordingAndMatchesManager()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync(CrmTelephonyProviders.Asterisk);
        var audio = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00 };

        var result = await harness.Sut.ReceiveAsteriskCallAsync(
            receiver.PublicId,
            receiver.Secret,
            new AsteriskCallWebhookPayload(
                "asterisk-100",
                "201",
                "+7 (999) 111-22-33",
                "outbound",
                "201",
                "1786968000",
                "27"),
            new MemoryStream(audio),
            audio.Length,
            "asterisk-100.wav",
            "audio/wav");

        Assert.Equal(SipoutCallReceiveOutcome.Accepted, result.Outcome);
        Assert.Equal(harness.CardId, result.CardId);
        var call = Assert.Single(await harness.Db.CrmCalls.ToListAsync());
        Assert.Equal(CrmTelephonyProviders.Asterisk, call.Provider);
        Assert.Equal(Harness.ManagerId, call.ManagerUserId);
        Assert.Equal("audio/wav", call.RecordingContentType);
        Assert.Equal("asterisk-100.wav", call.RecordingFileName);
        Assert.NotNull(call.RecordingStoragePath);
        Assert.Equal(audio, await File.ReadAllBytesAsync(Path.Combine(harness.RecordingPath, call.RecordingStoragePath)));
    }

    [Fact]
    public async Task AsteriskMissedCall_IsSavedWithoutRecording()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync(CrmTelephonyProviders.Asterisk);

        var result = await harness.Sut.ReceiveAsteriskCallAsync(
            receiver.PublicId,
            receiver.Secret,
            new AsteriskCallWebhookPayload(
                "asterisk-missed-101",
                "+7 (999) 111-22-33",
                "201",
                "inbound",
                "201",
                "1786968000",
                "0"),
            recording: null,
            recordingLength: 0,
            recordingFileName: null,
            recordingContentType: null);

        Assert.Equal(SipoutCallReceiveOutcome.Accepted, result.Outcome);
        Assert.Equal(harness.CardId, result.CardId);
        var call = Assert.Single(await harness.Db.CrmCalls.ToListAsync());
        Assert.Equal(CrmCallDirections.Incoming, call.Direction);
        Assert.Null(call.RecordingStoragePath);
        Assert.Null(call.RecordingContentType);
    }

    [Fact]
    public async Task AsteriskInboundRoute_PrefersLatestOutboundManagerForThirtyDays_ThenFallsBackToShift()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync(CrmTelephonyProviders.Asterisk);
        await harness.SetManagerOnShiftAsync(Harness.ManagerId);
        await harness.AddAsteriskManagerAsync("fallback-manager", "202", onShift: true);
        await harness.AddAsteriskCallAsync(
            "affinity-outbound",
            CrmCallDirections.Outgoing,
            Harness.ManagerId,
            "201",
            harness.Now.UtcDateTime.AddDays(-29));

        var route = await harness.Sut.ResolveAsteriskInboundRouteAsync(
            receiver.PublicId,
            receiver.Secret,
            "+7 (999) 111-22-33",
            "74950000000");

        Assert.Equal(AsteriskInboundRouteOutcome.Resolved, route.Outcome);
        Assert.Equal("201", route.PreferredExtension);
        Assert.Equal(["202"], route.FallbackExtensions);
        Assert.Equal(harness.Now.UtcDateTime.AddDays(1), route.AffinityExpiresAtUtc);
    }

    [Fact]
    public async Task AsteriskInboundRoute_FallbackAnswerDoesNotReplaceOutboundAffinity()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync(CrmTelephonyProviders.Asterisk);
        await harness.SetManagerOnShiftAsync(Harness.ManagerId);
        await harness.AddAsteriskManagerAsync("fallback-manager", "202", onShift: true);
        await harness.AddAsteriskCallAsync(
            "affinity-outbound",
            CrmCallDirections.Outgoing,
            Harness.ManagerId,
            "201",
            harness.Now.UtcDateTime.AddDays(-2));
        await harness.AddAsteriskCallAsync(
            "fallback-inbound",
            CrmCallDirections.Incoming,
            "fallback-manager",
            "202",
            harness.Now.UtcDateTime.AddMinutes(-1));

        var route = await harness.Sut.ResolveAsteriskInboundRouteAsync(
            receiver.PublicId,
            receiver.Secret,
            "79991112233",
            "74950000000");

        Assert.Equal(AsteriskInboundRouteOutcome.Resolved, route.Outcome);
        Assert.Equal("201", route.PreferredExtension);
        Assert.Equal(["202"], route.FallbackExtensions);
    }

    [Fact]
    public async Task AsteriskInboundRoute_IgnoresExpiredAffinityAndNeverFallsBackOffShift()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync(CrmTelephonyProviders.Asterisk);
        await harness.AddAsteriskManagerAsync("off-shift-manager", "202", onShift: false);
        await harness.AddAsteriskCallAsync(
            "expired-outbound",
            CrmCallDirections.Outgoing,
            Harness.ManagerId,
            "201",
            harness.Now.UtcDateTime.AddDays(-31));

        var route = await harness.Sut.ResolveAsteriskInboundRouteAsync(
            receiver.PublicId,
            receiver.Secret,
            "79991112233",
            "74950000000");

        Assert.Equal(AsteriskInboundRouteOutcome.Resolved, route.Outcome);
        Assert.Null(route.PreferredExtension);
        Assert.Empty(route.FallbackExtensions ?? []);
        Assert.Null(route.AffinityExpiresAtUtc);
    }

    [Fact]
    public async Task AsteriskInboundRoute_RejectsWrongSecret()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync(CrmTelephonyProviders.Asterisk);

        var route = await harness.Sut.ResolveAsteriskInboundRouteAsync(
            receiver.PublicId,
            "wrong-secret",
            "79991112233",
            "74950000000");

        Assert.Equal(AsteriskInboundRouteOutcome.Unauthorized, route.Outcome);
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
        public string RecordingPath { get; }

        private Harness(OrbitaDbContext db, Guid officeId, Guid personId, Guid cardId, string recordingPath)
        {
            Db = db;
            OfficeId = officeId;
            PersonId = personId;
            CardId = cardId;
            RecordingPath = recordingPath;
            var storage = new CrmCallRecordingStorageService(Options.Create(new CrmCallRecordingOptions
            {
                DataPath = recordingPath,
                MaxUploadBytes = 1024 * 1024
            }));
            Sut = new CrmTelephonyService(
                db,
                new PhoneNormalizer(),
                new FixedTimeProvider(Now),
                callRecordingStorage: storage);
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
            var recordingPath = Path.Combine(
                Path.GetTempPath(),
                "orbita-call-recording-tests",
                Guid.NewGuid().ToString("N"));
            return new Harness(db, officeId, personId, cardId, recordingPath);
        }

        public async Task<(Guid PublicId, string Secret)> CreateReceiverAsync(
            string provider = CrmTelephonyProviders.Sipout)
        {
            var (receiver, error) = await Sut.RotateReceiverAsync(
                OfficeId,
                "https://orbita.test",
                provider: provider);
            Assert.Null(error);
            Assert.NotNull(receiver);
            string secret;
            if (provider is CrmTelephonyProviders.Plusofon or CrmTelephonyProviders.Asterisk)
            {
                secret = Assert.IsType<string>(receiver.WebhookSecret);
            }
            else
            {
                var marker = "?secret=";
                var start = receiver.SipoutWebRequestUrl.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                var end = receiver.SipoutWebRequestUrl.IndexOf('&', start);
                secret = Uri.UnescapeDataString(receiver.SipoutWebRequestUrl[start..end]);
            }
            var (binding, bindingError) = await Sut.SetBindingAsync(
                OfficeId,
                ManagerId,
                "201",
                provider: provider);
            Assert.Null(bindingError);
            Assert.NotNull(binding);
            return (receiver.PublicId, secret);
        }

        public async Task SetManagerOnShiftAsync(string userId)
        {
            var profile = await Db.PanelUserProfiles.SingleAsync(x => x.UserId == userId);
            profile.CrmShiftActive = true;
            profile.CrmShiftStartedAtUtc = Now.UtcDateTime.AddHours(-1);
            await Db.SaveChangesAsync();
        }

        public async Task AddAsteriskManagerAsync(string userId, string extension, bool onShift)
        {
            Db.Users.Add(new IdentityUser
            {
                Id = userId,
                UserName = $"{userId}@orbita.local",
                NormalizedUserName = $"{userId}@orbita.local".ToUpperInvariant(),
                Email = $"{userId}@orbita.local",
                NormalizedEmail = $"{userId}@orbita.local".ToUpperInvariant()
            });
            Db.PanelUserProfiles.Add(new PanelUserProfileEntity
            {
                UserId = userId,
                OfficeId = OfficeId,
                FullName = userId,
                CrmShiftActive = onShift,
                CrmShiftStartedAtUtc = onShift ? Now.UtcDateTime.AddMinutes(-30) : null
            });
            await Db.SaveChangesAsync();
            var (binding, error) = await Sut.SetBindingAsync(
                OfficeId,
                userId,
                extension,
                provider: CrmTelephonyProviders.Asterisk);
            Assert.Null(error);
            Assert.NotNull(binding);
        }

        public async Task AddAsteriskCallAsync(
            string externalCallId,
            string direction,
            string managerUserId,
            string extension,
            DateTime startedAtUtc)
        {
            Db.CrmCalls.Add(new CrmCallEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = OfficeId,
                CardId = CardId,
                Provider = CrmTelephonyProviders.Asterisk,
                ExternalCallId = externalCallId,
                Direction = direction,
                CallerPhone = direction == CrmCallDirections.Outgoing ? extension : "79991112233",
                CalledPhone = direction == CrmCallDirections.Outgoing ? "79991112233" : extension,
                ClientPhoneNormalized = "79991112233",
                ProviderUserKey = extension,
                ManagerUserId = managerUserId,
                StartedAtUtc = startedAtUtc,
                DurationSeconds = 30,
                ReceivedAtUtc = startedAtUtc,
                UpdatedAtUtc = startedAtUtc
            });
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (Directory.Exists(RecordingPath))
            {
                Directory.Delete(RecordingPath, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

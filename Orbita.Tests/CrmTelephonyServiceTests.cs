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
    public void IceCredentialFactory_CreatesCoturnRestApiCredential()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var (username, credential) = CrmTelephonyIceCredentialFactory.Create(
            "test-secret",
            "301",
            now,
            3600);

        Assert.Equal("1700003600:301", username);
        Assert.Equal("MyiicCuKr1PN6dMdoKGrj6PLVHY=", credential);
    }

    [Fact]
    public async Task AsteriskBinding_GeneratesEncryptedWebRtcCredentials_AndPublishesRuntimeEndpoint()
    {
        var runtimePath = Path.Combine(Path.GetTempPath(), "orbita-webrtc-runtime-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase($"crm-telephony-webrtc-{Guid.NewGuid():N}")
                .Options;
            await using var db = new OrbitaDbContext(options);
            var officeId = Guid.NewGuid();
            const string userId = "webrtc-manager";
            db.Offices.Add(new OfficeEntity
            {
                Id = officeId,
                Name = "WebRTC office",
                RegistrationSecretHash = "hash",
                IsEnabled = true,
                CrmEnabled = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            db.Users.Add(new IdentityUser
            {
                Id = userId,
                UserName = "webrtc-manager@orbita.local",
                NormalizedUserName = "WEBRTC-MANAGER@ORBITA.LOCAL",
                Email = "webrtc-manager@orbita.local",
                NormalizedEmail = "WEBRTC-MANAGER@ORBITA.LOCAL"
            });
            db.PanelUserProfiles.Add(new PanelUserProfileEntity
            {
                UserId = userId,
                OfficeId = officeId,
                FullName = "WebRTC manager"
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

            var (binding, bindingError) = await sut.SetBindingAsync(
                officeId,
                userId,
                "201",
                provider: CrmTelephonyProviders.Asterisk);
            Assert.NotNull(binding);
            Assert.Null(bindingError);

            var (endpoint, endpointError) = await sut.GetOrProvisionWebRtcEndpointAsync(officeId, userId);
            Assert.NotNull(endpoint);
            Assert.Null(endpointError);
            Assert.Equal("201", endpoint.Extension);
            Assert.Equal("201-webrtc", endpoint.AuthorizationUsername);
            Assert.True(endpoint.Password.Length >= 16);

            var stored = await db.CrmTelephonyUserBindings.SingleAsync();
            Assert.Equal("201-webrtc", stored.WebRtcAuthorizationUsername);
            Assert.NotNull(stored.WebRtcPasswordProtected);
            Assert.DoesNotContain(endpoint.Password, stored.WebRtcPasswordProtected, StringComparison.Ordinal);
            Assert.Equal(endpoint.Password, protector.Unprotect(stored.WebRtcPasswordProtected));

            var runtimeConfig = await File.ReadAllTextAsync(
                Path.Combine(runtimePath, $"webrtc.{officeId:D}.conf"));
            Assert.Contains("[201-webrtc]", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("username=201-webrtc", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains($"password={endpoint.Password}", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("callerid=201 <201>", runtimeConfig, StringComparison.Ordinal);

            var (sameEndpoint, sameEndpointError) = await sut.GetOrProvisionWebRtcEndpointAsync(officeId, userId);
            Assert.NotNull(sameEndpoint);
            Assert.Null(sameEndpointError);
            Assert.Equal(endpoint.Password, sameEndpoint.Password);

            var (updatedBinding, updatedBindingError) = await sut.SetBindingAsync(
                officeId,
                userId,
                "301",
                provider: CrmTelephonyProviders.Asterisk);
            Assert.NotNull(updatedBinding);
            Assert.Null(updatedBindingError);

            var (updatedEndpoint, updatedEndpointError) = await sut.GetOrProvisionWebRtcEndpointAsync(
                officeId,
                userId);
            Assert.NotNull(updatedEndpoint);
            Assert.Null(updatedEndpointError);
            Assert.Equal("301", updatedEndpoint.Extension);
            Assert.Equal("301-webrtc", updatedEndpoint.AuthorizationUsername);
            Assert.NotEqual(endpoint.Password, updatedEndpoint.Password);

            runtimeConfig = await File.ReadAllTextAsync(
                Path.Combine(runtimePath, $"webrtc.{officeId:D}.conf"));
            Assert.Contains("[301-webrtc]", runtimeConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("[201-webrtc]", runtimeConfig, StringComparison.Ordinal);
            Assert.Equal(
                "301=default\n",
                (await File.ReadAllTextAsync(Path.Combine(runtimePath, $"routes.{officeId:D}.conf")))
                    .Replace("\r\n", "\n"));

            Assert.True(await sut.RemoveBindingAsync(
                officeId,
                userId,
                provider: CrmTelephonyProviders.Asterisk));
            runtimeConfig = await File.ReadAllTextAsync(
                Path.Combine(runtimePath, $"webrtc.{officeId:D}.conf"));
            Assert.DoesNotContain("[201-webrtc]", runtimeConfig, StringComparison.Ordinal);
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
    public async Task SipRuntimeWriter_AllowsConcurrentWebRtcEndpointPublications()
    {
        var runtimePath = Path.Combine(Path.GetTempPath(), "orbita-webrtc-runtime-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new CrmSipRuntimeConfigWriter(Options.Create(new CrmSipRuntimeOptions
            {
                ConfigPath = runtimePath
            }));
            var officeId = Guid.NewGuid();
            var endpoints = new[]
            {
                new CrmAsteriskWebRtcEndpoint("201", "201-webrtc", "0123456789abcdef0123456789abcdef")
            };

            var publications = Enumerable.Range(0, 32)
                .Select(_ => writer.WriteWebRtcEndpointsAsync(officeId, endpoints));
            var results = await Task.WhenAll(publications);

            Assert.All(results, result => Assert.True(result.Success, result.Error));
            var config = await File.ReadAllTextAsync(Path.Combine(runtimePath, $"webrtc.{officeId:D}.conf"));
            Assert.Contains("[201-webrtc]", config, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(runtimePath))
            {
                Directory.Delete(runtimePath, true);
            }
        }
    }

    [Fact]
    public async Task SipRuntimeWriter_ReadsProviderRejectionDetail()
    {
        var runtimePath = Path.Combine(Path.GetTempPath(), "orbita-sip-runtime-status-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var officeId = Guid.NewGuid();
            Directory.CreateDirectory(runtimePath);
            await File.WriteAllTextAsync(
                Path.Combine(runtimePath, $"beeline.{officeId:D}.status"),
                "shared=rejected\nshared.detail=403 Forbidden\n2026-08-23T10:00:00Z\n");
            var writer = new CrmSipRuntimeConfigWriter(Options.Create(new CrmSipRuntimeOptions
            {
                ConfigPath = runtimePath
            }));

            var status = await writer.ReadBeelineStatusAsync(officeId, "shared");

            Assert.Equal("rejected", status.Status);
            Assert.Equal("403 Forbidden", status.Detail);
            Assert.Equal(new DateTime(2026, 8, 23, 10, 0, 0, DateTimeKind.Utc), status.CheckedAtUtc);
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
            Assert.Contains("line=yes", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains($"endpoint=beeline-{endpointKey}", runtimeConfig, StringComparison.Ordinal);
            Assert.DoesNotContain($"[beeline-{endpointKey}-identify]", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains($"set_var=ORBITA_OFFICE_ID={officeId:D}", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("username=auth-user@beeline.test", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("password=test-secret", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("contact=sip:sip.beeline.test:5060", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("server_uri=sip:sip.beeline.test:5060", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("client_uri=sip:sip-user@beeline.test", runtimeConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("outbound_proxy=", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("expiration=120", runtimeConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("expiration=300", runtimeConfig, StringComparison.Ordinal);
            Assert.Equal("beeline\n", await File.ReadAllTextAsync(Path.Combine(runtimePath, $"beeline.{officeId:D}.outbound")));

            await writer.WriteUserOutboundRoutesAsync(
                officeId,
                new Dictionary<string, string>
                {
                    ["201"] = CrmTelephonyOutboundProviders.ForBeelineLine("default")
                });
            Assert.Equal(
                "201=beeline\n",
                (await File.ReadAllTextAsync(Path.Combine(runtimePath, $"routes.{officeId:D}.conf"))).Replace("\r\n", "\n"));

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
            Assert.Contains("server_uri=sip:new-sip.beeline.test:5060", runtimeConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("outbound_proxy=", runtimeConfig, StringComparison.Ordinal);
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
    public async Task BeelineSipAccounts_SupportSharedAndPersonalLines_AndPersonalLineHasOneOwner()
    {
        var runtimePath = Path.Combine(Path.GetTempPath(), "orbita-sip-runtime-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase($"crm-telephony-beeline-lines-{Guid.NewGuid():N}")
                .Options;
            await using var db = new OrbitaDbContext(options);
            var officeId = Guid.NewGuid();
            db.Offices.Add(new OfficeEntity
            {
                Id = officeId,
                Name = "Beeline lines office",
                RegistrationSecretHash = "hash",
                IsEnabled = true,
                CrmEnabled = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            foreach (var (userId, name) in new[] { ("manager-one", "Менеджер Один"), ("manager-two", "Менеджер Два") })
            {
                db.Users.Add(new IdentityUser
                {
                    Id = userId,
                    UserName = $"{userId}@orbita.local",
                    NormalizedUserName = $"{userId}@orbita.local".ToUpperInvariant(),
                    Email = $"{userId}@orbita.local",
                    NormalizedEmail = $"{userId}@orbita.local".ToUpperInvariant()
                });
                db.PanelUserProfiles.Add(new PanelUserProfileEntity
                {
                    UserId = userId,
                    OfficeId = officeId,
                    FullName = name
                });
            }
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

            var shared = await sut.UpsertBeelineSipAccountAsync(
                officeId,
                "shared",
                new UpdateSipProviderAccountRequest(
                    "shared.proxy.test", "beeline.test", 5060, "udp", "shared-user", "shared-auth",
                    "shared-password", true, "Общая многоканальная", CrmSipAccountModes.Shared));
            var personal = await sut.UpsertBeelineSipAccountAsync(
                officeId,
                "personal1",
                new UpdateSipProviderAccountRequest(
                    "personal.proxy.test", "beeline.test", 5060, "udp", "personal-user", "personal-auth",
                    "personal-password", false, "Личная линия", CrmSipAccountModes.Personal));
            Assert.True(shared.Success, shared.Error);
            Assert.True(personal.Success, personal.Error);

            var personalProvider = CrmTelephonyOutboundProviders.ForBeelineLine("personal1");
            var (firstBinding, firstError) = await sut.SetBindingAsync(
                officeId, "manager-one", "201", provider: CrmTelephonyProviders.Asterisk,
                outboundProvider: personalProvider);
            Assert.NotNull(firstBinding);
            Assert.Null(firstError);

            var (secondBinding, secondError) = await sut.SetBindingAsync(
                officeId, "manager-two", "202", provider: CrmTelephonyProviders.Asterisk,
                outboundProvider: personalProvider);
            Assert.Null(secondBinding);
            Assert.Equal("Персональная линия Билайна уже назначена другому сотруднику.", secondError);

            var settings = await sut.GetSettingsAsync(officeId, provider: CrmTelephonyProviders.Beeline);
            Assert.NotNull(settings);
            Assert.Equal(2, settings.SipAccounts?.Count);
            var personalDto = Assert.Single(settings.SipAccounts!, account => account.AccountKey == "personal1");
            Assert.Equal("manager-one", personalDto.AssignedUserId);
            Assert.Equal("Менеджер Один", personalDto.AssignedUserName);

            var runtimeConfig = await File.ReadAllTextAsync(Path.Combine(runtimePath, $"beeline.{officeId:D}.conf"));
            Assert.Contains($"[beeline-{officeId:N}-shared]", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains($"[beeline-{officeId:N}-personal1]", runtimeConfig, StringComparison.Ordinal);
            Assert.Equal(
                $"personal1=beeline-{officeId:N}-personal1-registration\nshared=beeline-{officeId:N}-shared-registration\n",
                (await File.ReadAllTextAsync(Path.Combine(runtimePath, $"beeline.{officeId:D}.accounts"))).Replace("\r\n", "\n"));
            Assert.Equal(
                $"beeline-{officeId:N}-shared\n",
                (await File.ReadAllTextAsync(Path.Combine(runtimePath, $"beeline.{officeId:D}.outbound"))).Replace("\r\n", "\n"));
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
    public async Task BeelineSipAccounts_AreStoredAndWrittenSeparatelyForEachOffice()
    {
        var runtimePath = Path.Combine(Path.GetTempPath(), "orbita-sip-runtime-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase($"crm-telephony-beeline-offices-{Guid.NewGuid():N}")
                .Options;
            await using var db = new OrbitaDbContext(options);
            var firstOfficeId = Guid.NewGuid();
            var thirdOfficeId = Guid.NewGuid();
            db.Offices.AddRange(
                new OfficeEntity
                {
                    Id = firstOfficeId,
                    Name = "First office",
                    RegistrationSecretHash = "hash-1",
                    IsEnabled = true,
                    CrmEnabled = true,
                    CreatedAtUtc = DateTime.UtcNow
                },
                new OfficeEntity
                {
                    Id = thirdOfficeId,
                    Name = "Third office",
                    RegistrationSecretHash = "hash-3",
                    IsEnabled = true,
                    CrmEnabled = true,
                    CreatedAtUtc = DateTime.UtcNow
                });
            await db.SaveChangesAsync();

            var writer = new CrmSipRuntimeConfigWriter(Options.Create(new CrmSipRuntimeOptions
            {
                ConfigPath = runtimePath
            }));
            var sut = new CrmTelephonyService(
                db,
                new PhoneNormalizer(),
                TimeProvider.System,
                credentialProtector: new CrmTelephonyCredentialProtector(new EphemeralDataProtectionProvider()),
                sipRuntimeConfigWriter: writer);

            var first = await sut.UpsertBeelineSipAccountAsync(
                firstOfficeId,
                "first-line",
                new UpdateSipProviderAccountRequest(
                    "first.proxy.test", "beeline.test", 5060, "udp", "first-user", "first-auth",
                    "first-password", true, "Линия первого офиса", CrmSipAccountModes.Shared));
            var third = await sut.UpsertBeelineSipAccountAsync(
                thirdOfficeId,
                "third-line",
                new UpdateSipProviderAccountRequest(
                    "third.proxy.test", "beeline.test", 5060, "udp", "third-user", "third-auth",
                    "third-password", true, "Линия третьего офиса", CrmSipAccountModes.Shared));

            Assert.True(first.Success, first.Error);
            Assert.True(third.Success, third.Error);

            var firstSettings = await sut.GetSettingsAsync(firstOfficeId, provider: CrmTelephonyProviders.Beeline);
            var thirdSettings = await sut.GetSettingsAsync(thirdOfficeId, provider: CrmTelephonyProviders.Beeline);
            var firstAccount = Assert.Single(firstSettings!.SipAccounts!);
            var thirdAccount = Assert.Single(thirdSettings!.SipAccounts!);
            Assert.Equal("first-user", firstAccount.SipLogin);
            Assert.Equal("third-user", thirdAccount.SipLogin);

            var firstRuntime = await File.ReadAllTextAsync(Path.Combine(runtimePath, $"beeline.{firstOfficeId:D}.conf"));
            var thirdRuntime = await File.ReadAllTextAsync(Path.Combine(runtimePath, $"beeline.{thirdOfficeId:D}.conf"));
            Assert.Contains("first-user", firstRuntime, StringComparison.Ordinal);
            Assert.DoesNotContain("third-user", firstRuntime, StringComparison.Ordinal);
            Assert.Contains("third-user", thirdRuntime, StringComparison.Ordinal);
            Assert.DoesNotContain("first-user", thirdRuntime, StringComparison.Ordinal);
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
    public async Task PlusofonSipAccount_StoresCallerIdSeparatelyFromRecordingApiCredentials()
    {
        var runtimePath = Path.Combine(Path.GetTempPath(), "orbita-sip-runtime-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase($"crm-telephony-plusofon-{Guid.NewGuid():N}")
                .Options;
            await using var db = new OrbitaDbContext(options);
            var officeId = Guid.NewGuid();
            db.Offices.Add(new OfficeEntity
            {
                Id = officeId,
                Name = "Plusofon office",
                RegistrationSecretHash = "hash",
                IsEnabled = true,
                CrmEnabled = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var writer = new CrmSipRuntimeConfigWriter(Options.Create(new CrmSipRuntimeOptions
            {
                ConfigPath = runtimePath
            }));
            var sut = new CrmTelephonyService(
                db,
                new PhoneNormalizer(),
                TimeProvider.System,
                credentialProtector: new CrmTelephonyCredentialProtector(new EphemeralDataProtectionProvider()),
                sipRuntimeConfigWriter: writer);

            var sipResult = await sut.SetSipProviderAccountAsync(
                officeId,
                CrmTelephonyProviders.Plusofon,
                new UpdateSipProviderAccountRequest(
                    "12345.voice.plusofon.ru",
                    null,
                    5060,
                    "tcp",
                    "210123456789",
                    string.Empty,
                    "sip-secret",
                    true,
                    OutboundCallerId: "+7 (495) 133-22-10"));
            Assert.True(sipResult.Success, sipResult.Error);

            var apiResult = await sut.SetPlusofonCredentialsAsync(officeId, "client-id", "recording-token");
            Assert.True(apiResult.Success, apiResult.Error);

            var stored = Assert.Single(await db.CrmTelephonyWebhooks.ToListAsync());
            Assert.DoesNotContain("sip-secret", stored.SipAccountProtected, StringComparison.Ordinal);
            Assert.DoesNotContain("recording-token", stored.ProviderAccessTokenProtected, StringComparison.Ordinal);
            Assert.Equal("client-id", stored.ProviderClientId);

            var endpointKey = officeId.ToString("N");
            var runtimeConfig = await File.ReadAllTextAsync(Path.Combine(runtimePath, $"plusofon.{officeId:D}.conf"));
            Assert.Contains($"[plusofon-{endpointKey}]", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains($"[plusofon-{endpointKey}-registration]", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("line=yes", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains($"endpoint=plusofon-{endpointKey}", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("transport=transport-tcp", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("server_uri=sip:12345.voice.plusofon.ru:5060", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("username=210123456789", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("fatal_retry_interval=60", runtimeConfig, StringComparison.Ordinal);
            Assert.Contains("auth_rejection_permanent=no", runtimeConfig, StringComparison.Ordinal);
            Assert.Equal(
                "74951332210\n12345.voice.plusofon.ru\n",
                (await File.ReadAllTextAsync(Path.Combine(runtimePath, $"plusofon.{officeId:D}.callerid"))).Replace("\r\n", "\n"));
            Assert.Equal(
                "plusofon\n",
                (await File.ReadAllTextAsync(Path.Combine(runtimePath, $"outbound.{officeId:D}.conf"))).Replace("\r\n", "\n"));

            var settings = await sut.GetSettingsAsync(officeId, provider: CrmTelephonyProviders.Plusofon);
            Assert.NotNull(settings?.SipAccount);
            Assert.Equal("74951332210", settings.SipAccount.OutboundCallerId);
            Assert.True(settings.SipAccount.PasswordConfigured);
            Assert.True(settings.ProviderCredentialsConfigured);
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
    public async Task SipoutPlusofonAndBeeline_StayConnected_WhenOfficeDefaultIsSwitched()
    {
        var runtimePath = Path.Combine(Path.GetTempPath(), "orbita-sip-runtime-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase($"crm-telephony-provider-switch-{Guid.NewGuid():N}")
                .Options;
            await using var db = new OrbitaDbContext(options);
            var officeId = Guid.NewGuid();
            db.Offices.Add(new OfficeEntity
            {
                Id = officeId,
                Name = "Provider switch office",
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

            var plusofon = await sut.UpsertPlusofonSipAccountAsync(
                officeId,
                "main",
                new UpdateSipProviderAccountRequest(
                    "12345.voice.plusofon.ru", "12345.voice.plusofon.ru", 5060, "tcp",
                    "plus-user", "plus-auth", "plus-password", true,
                    "Плюсофон", CrmSipAccountModes.Shared, "74951332210"));
            Assert.True(plusofon.Success, plusofon.Error);

            var beelineConnected = await sut.UpsertBeelineSipAccountAsync(
                officeId,
                "main",
                new UpdateSipProviderAccountRequest(
                    "sip.beeline.test", "beeline.test", 5060, "udp",
                    "beeline-user", "beeline-auth", "beeline-password", false,
                    "Билайн", CrmSipAccountModes.Shared));
            Assert.True(beelineConnected.Success, beelineConnected.Error);

            var sipoutConnected = await sut.UpsertSipoutSipAccountAsync(
                officeId,
                "ilya",
                new UpdateSipProviderAccountRequest(
                    "sip.sipout.net", "sip.sipout.net", 5060, "udp",
                    "1759571735121", "1759571735121", "sipout-password", false,
                    "SIPOUT Илья", CrmSipAccountModes.Shared, "79010786287", "202"));
            Assert.True(sipoutConnected.Success, sipoutConnected.Error);

            var plusSettings = await sut.GetSettingsAsync(officeId, provider: CrmTelephonyProviders.Plusofon);
            var beelineSettings = await sut.GetSettingsAsync(officeId, provider: CrmTelephonyProviders.Beeline);
            var sipoutSettings = await sut.GetSettingsAsync(officeId, provider: CrmTelephonyProviders.Sipout);
            Assert.True(plusSettings?.IsConfigured);
            Assert.True(beelineSettings?.IsConfigured);
            Assert.True(sipoutSettings?.IsConfigured);
            Assert.True(Assert.Single(plusSettings!.SipAccounts!).UseForOutbound);
            Assert.False(Assert.Single(beelineSettings!.SipAccounts!).UseForOutbound);
            var sipoutAccount = Assert.Single(sipoutSettings!.SipAccounts!);
            Assert.False(sipoutAccount.UseForOutbound);
            Assert.Equal("202", sipoutAccount.InternalNumber);
            Assert.Contains(
                "plusofon",
                await File.ReadAllTextAsync(Path.Combine(runtimePath, $"outbound.{officeId:D}.conf")),
                StringComparison.Ordinal);

            var beelineSelected = await sut.SetOfficeDefaultOutboundAsync(
                officeId,
                CrmTelephonyOutboundProviders.ForBeelineLine("main"));
            Assert.True(beelineSelected.Success, beelineSelected.Error);

            plusSettings = await sut.GetSettingsAsync(officeId, provider: CrmTelephonyProviders.Plusofon);
            beelineSettings = await sut.GetSettingsAsync(officeId, provider: CrmTelephonyProviders.Beeline);
            Assert.True(plusSettings?.IsConfigured);
            Assert.True(beelineSettings?.IsConfigured);
            Assert.False(Assert.Single(plusSettings!.SipAccounts!).UseForOutbound);
            Assert.True(Assert.Single(beelineSettings!.SipAccounts!).UseForOutbound);
            Assert.True(File.Exists(Path.Combine(runtimePath, $"plusofon.{officeId:D}.conf")));
            Assert.True(File.Exists(Path.Combine(runtimePath, $"beeline.{officeId:D}.conf")));
            Assert.True(File.Exists(Path.Combine(runtimePath, $"sipout.{officeId:D}.conf")));
            var sipoutConfig = await File.ReadAllTextAsync(Path.Combine(runtimePath, $"sipout.{officeId:D}.conf"));
            Assert.Contains("username=1759571735121", sipoutConfig, StringComparison.Ordinal);
            Assert.Contains("contact_user=202", sipoutConfig, StringComparison.Ordinal);
            Assert.Contains("set_var=ORBITA_INBOUND_PROVIDER=sipout", sipoutConfig, StringComparison.Ordinal);
            Assert.Contains(
                "beeline",
                await File.ReadAllTextAsync(Path.Combine(runtimePath, $"outbound.{officeId:D}.conf")),
                StringComparison.Ordinal);

            var sipoutSelected = await sut.SetOfficeDefaultOutboundAsync(
                officeId,
                CrmTelephonyOutboundProviders.ForSipoutLine("ilya"));
            Assert.True(sipoutSelected.Success, sipoutSelected.Error);
            plusSettings = await sut.GetSettingsAsync(officeId, provider: CrmTelephonyProviders.Plusofon);
            beelineSettings = await sut.GetSettingsAsync(officeId, provider: CrmTelephonyProviders.Beeline);
            sipoutSettings = await sut.GetSettingsAsync(officeId, provider: CrmTelephonyProviders.Sipout);
            Assert.False(Assert.Single(plusSettings!.SipAccounts!).UseForOutbound);
            Assert.False(Assert.Single(beelineSettings!.SipAccounts!).UseForOutbound);
            Assert.True(Assert.Single(sipoutSettings!.SipAccounts!).UseForOutbound);
            Assert.Contains(
                "sipout",
                await File.ReadAllTextAsync(Path.Combine(runtimePath, $"outbound.{officeId:D}.conf")),
                StringComparison.Ordinal);
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
    public async Task AsteriskInboundRoute_PersonalPlusofonLineTargetsItsAssignedManager()
    {
        var runtimePath = Path.Combine(Path.GetTempPath(), "orbita-sip-runtime-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase($"crm-telephony-personal-inbound-{Guid.NewGuid():N}")
                .Options;
            await using var db = new OrbitaDbContext(options);
            var officeId = Guid.NewGuid();
            const string managerId = "personal-plusofon-manager";
            db.Offices.Add(new OfficeEntity
            {
                Id = officeId,
                Name = "Personal Plusofon office",
                RegistrationSecretHash = "hash",
                IsEnabled = true,
                CrmEnabled = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            db.Users.Add(new IdentityUser
            {
                Id = managerId,
                UserName = "personal-plusofon-manager@orbita.local",
                NormalizedUserName = "PERSONAL-PLUSOFON-MANAGER@ORBITA.LOCAL",
                Email = "personal-plusofon-manager@orbita.local",
                NormalizedEmail = "PERSONAL-PLUSOFON-MANAGER@ORBITA.LOCAL"
            });
            db.PanelUserProfiles.Add(new PanelUserProfileEntity
            {
                UserId = managerId,
                OfficeId = officeId,
                FullName = "Персональный менеджер"
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
            var line = await sut.UpsertPlusofonSipAccountAsync(
                officeId,
                "personal1",
                new UpdateSipProviderAccountRequest(
                    "12345.voice.plusofon.ru", "12345.voice.plusofon.ru", 5060, "tcp",
                    "personal-user", "personal-auth", "personal-password", false,
                    "Личная линия", CrmSipAccountModes.Personal, "74951332210"));
            Assert.True(line.Success, line.Error);
            var (binding, bindingError) = await sut.SetBindingAsync(
                officeId,
                managerId,
                "301",
                provider: CrmTelephonyProviders.Asterisk,
                outboundProvider: CrmTelephonyOutboundProviders.ForPlusofonLine("personal1"));
            Assert.NotNull(binding);
            Assert.Null(bindingError);
            var (receiver, receiverError) = await sut.RotateReceiverAsync(
                officeId,
                "https://orbita.test",
                provider: CrmTelephonyProviders.Asterisk);
            Assert.NotNull(receiver);
            Assert.Null(receiverError);
            Assert.True(await sut.SetEnabledAsync(officeId, true, provider: CrmTelephonyProviders.Asterisk));

            var route = await sut.ResolveAsteriskInboundRouteAsync(
                receiver.PublicId,
                receiver.WebhookSecret,
                "79991112233",
                "74951332210",
                CrmTelephonyProviders.Plusofon,
                "personal1");

            Assert.Equal(AsteriskInboundRouteOutcome.Resolved, route.Outcome);
            Assert.Equal("301", route.PreferredExtension);
            Assert.Null(route.AffinityExpiresAtUtc);
        }
        finally
        {
            if (Directory.Exists(runtimePath))
            {
                Directory.Delete(runtimePath, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("тест", "plusofon.test", "210123456789", "210123456789", "SIP-сервер")]
    [InlineData("plusofon.test", "plusofon.test", "тест", "210123456789", "SIP-логин")]
    [InlineData("plusofon.test", "plusofon.test", "210123456789", "тест", "SIP-логин")]
    public async Task PlusofonSipAccount_RejectsValuesThatAsteriskCannotParse(
        string server,
        string domain,
        string sipLogin,
        string authorizationLogin,
        string expectedError)
    {
        var runtimePath = Path.Combine(Path.GetTempPath(), "orbita-sip-runtime-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase($"crm-telephony-invalid-sip-{Guid.NewGuid():N}")
                .Options;
            await using var db = new OrbitaDbContext(options);
            var officeId = Guid.NewGuid();
            db.Offices.Add(new OfficeEntity
            {
                Id = officeId,
                Name = "Invalid SIP office",
                RegistrationSecretHash = "hash",
                IsEnabled = true,
                CrmEnabled = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var sut = new CrmTelephonyService(
                db,
                new PhoneNormalizer(),
                TimeProvider.System,
                credentialProtector: new CrmTelephonyCredentialProtector(new EphemeralDataProtectionProvider()),
                sipRuntimeConfigWriter: new CrmSipRuntimeConfigWriter(Options.Create(new CrmSipRuntimeOptions
                {
                    ConfigPath = runtimePath
                })));

            var result = await sut.SetSipProviderAccountAsync(
                officeId,
                CrmTelephonyProviders.Plusofon,
                new UpdateSipProviderAccountRequest(
                    server,
                    domain,
                    5060,
                    "tcp",
                    sipLogin,
                    authorizationLogin,
                    "sip-secret",
                    true,
                    OutboundCallerId: "74951332210"));

            Assert.False(result.Success);
            Assert.Contains(expectedError, result.Error, StringComparison.Ordinal);
            Assert.Empty(await db.CrmTelephonyWebhooks.ToListAsync());
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
                Assert.Contains("qualify_frequency=0", config, StringComparison.Ordinal);
                Assert.DoesNotContain("qualify_frequency=30", config, StringComparison.Ordinal);
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
    public async Task Receiver_IsDisabledByDefault_AndEnabledStateIsIsolatedByOffice()
    {
        await using var harness = await Harness.CreateAsync();
        var secondOfficeId = Guid.NewGuid();
        harness.Db.Offices.Add(new OfficeEntity
        {
            Id = secondOfficeId,
            Name = "Second SIPOUT office",
            RegistrationSecretHash = "hash",
            IsEnabled = true,
            CrmEnabled = true,
            CreatedAtUtc = harness.Now.UtcDateTime
        });
        await harness.Db.SaveChangesAsync();

        var (firstReceiver, firstError) = await harness.Sut.RotateReceiverAsync(
            harness.OfficeId,
            "https://orbita.test");
        var (secondReceiver, secondError) = await harness.Sut.RotateReceiverAsync(
            secondOfficeId,
            "https://orbita.test");

        Assert.Null(firstError);
        Assert.NotNull(firstReceiver);
        Assert.Null(secondError);
        Assert.NotNull(secondReceiver);
        Assert.All(await harness.Db.CrmTelephonyWebhooks.ToListAsync(), receiver => Assert.False(receiver.IsEnabled));

        Assert.True(await harness.Sut.SetEnabledAsync(harness.OfficeId, true));
        var receivers = await harness.Db.CrmTelephonyWebhooks
            .OrderBy(x => x.OfficeId)
            .ToListAsync();
        Assert.True(receivers.Single(x => x.OfficeId == harness.OfficeId).IsEnabled);
        Assert.False(receivers.Single(x => x.OfficeId == secondOfficeId).IsEnabled);
    }

    [Fact]
    public async Task RotateReceiver_PreservesExistingEnabledState()
    {
        await using var harness = await Harness.CreateAsync();
        var (original, originalError) = await harness.Sut.RotateReceiverAsync(
            harness.OfficeId,
            "https://orbita.test");
        Assert.Null(originalError);
        Assert.NotNull(original);
        Assert.True(await harness.Sut.SetEnabledAsync(harness.OfficeId, true));

        var (rotatedWhileEnabled, enabledError) = await harness.Sut.RotateReceiverAsync(
            harness.OfficeId,
            "https://orbita.test");
        Assert.Null(enabledError);
        Assert.NotNull(rotatedWhileEnabled);
        Assert.NotEqual(original.PublicId, rotatedWhileEnabled.PublicId);
        Assert.True((await harness.Db.CrmTelephonyWebhooks.SingleAsync()).IsEnabled);

        Assert.True(await harness.Sut.SetEnabledAsync(harness.OfficeId, false));
        var (_, disabledError) = await harness.Sut.RotateReceiverAsync(
            harness.OfficeId,
            "https://orbita.test");
        Assert.Null(disabledError);
        Assert.False((await harness.Db.CrmTelephonyWebhooks.SingleAsync()).IsEnabled);
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
    public async Task AsteriskInboundRoute_WithSharedCallerId_PrefersManagerWhoCalledClientLast()
    {
        await using var harness = await Harness.CreateAsync();
        var receiver = await harness.CreateReceiverAsync(CrmTelephonyProviders.Asterisk);
        await harness.AddAsteriskManagerAsync("second-manager", "202", onShift: true);
        await harness.AddAsteriskCallAsync(
            "shared-aon-first",
            CrmCallDirections.Outgoing,
            Harness.ManagerId,
            "201",
            harness.Now.UtcDateTime.AddHours(-2));
        await harness.AddAsteriskCallAsync(
            "shared-aon-latest",
            CrmCallDirections.Outgoing,
            "second-manager",
            "202",
            harness.Now.UtcDateTime.AddHours(-1));

        var route = await harness.Sut.ResolveAsteriskInboundRouteAsync(
            receiver.PublicId,
            receiver.Secret,
            "79991112233",
            "74951332210");

        Assert.Equal(AsteriskInboundRouteOutcome.Resolved, route.Outcome);
        Assert.Equal("202", route.PreferredExtension);
        Assert.Equal(harness.Now.UtcDateTime.AddDays(30).AddHours(-1), route.AffinityExpiresAtUtc);
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
            Assert.True(await Sut.SetEnabledAsync(OfficeId, true, provider: provider));
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

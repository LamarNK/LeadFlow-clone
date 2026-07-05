using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class WebhookSecretProtectorTests
{
    [Fact]
    public void Protect_Unprotect_SurvivesProviderRecreation_WhenKeysPersisted()
    {
        var keysPath = Path.Combine(Path.GetTempPath(), "orbita-test-dp-" + Guid.NewGuid());
        Directory.CreateDirectory(keysPath);

        try
        {
            const string webhook = "https://example.bitrix24.ru/rest/1/secret/crm.deal.add.json";
            string protectedValue;

            var provider1 = DataProtectionProvider.Create(
                new DirectoryInfo(keysPath),
                configure =>
                {
                    configure.SetApplicationName("Orbita.Api");
                });
            protectedValue = new WebhookSecretProtector(provider1).Protect(webhook);

            var provider2 = DataProtectionProvider.Create(
                new DirectoryInfo(keysPath),
                configure =>
                {
                    configure.SetApplicationName("Orbita.Api");
                });
            var restored = new WebhookSecretProtector(provider2).Unprotect(protectedValue);

            Assert.Equal(webhook, restored);
        }
        finally
        {
            if (Directory.Exists(keysPath))
            {
                Directory.Delete(keysPath, recursive: true);
            }
        }
    }

    [Fact]
    public void Unprotect_FailsAfterProviderRecreation_WhenKeyRingChanges()
    {
        const string webhook = "https://example.bitrix24.ru/rest/1/secret/crm.deal.add.json";

        var providerBeforeRestart = CreateEphemeralProvider("Orbita.Api.before-restart");
        var protectedValue = new WebhookSecretProtector(providerBeforeRestart).Protect(webhook);

        var providerAfterRestart = CreateEphemeralProvider("Orbita.Api.after-restart");
        var protector = new WebhookSecretProtector(providerAfterRestart);

        Assert.ThrowsAny<Exception>(() => protector.Unprotect(protectedValue));
    }

    private static IDataProtectionProvider CreateEphemeralProvider(string applicationName) =>
        new ServiceCollection()
            .AddDataProtection()
            .SetApplicationName(applicationName)
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();
}
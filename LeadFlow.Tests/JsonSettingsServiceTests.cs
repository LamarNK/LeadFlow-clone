using LeadFlow.Models;
using LeadFlow.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class JsonSettingsServiceTests
{
    [Fact]
    public async Task LoadAsync_MissingFile_CreatesEncryptedFileAndReturnsDefaults()
    {
        var dir = CreateTempDataDir();
        try
        {
            var sut = new JsonSettingsService(dir);

            var settings = await sut.LoadAsync(CancellationToken.None);

            Assert.True(File.Exists(sut.GetSettingsPath()));
            Assert.False(settings.DemoModeEnabled);
            Assert.Equal(JsonSettingsService.FixedBitrixWebhookUrl, settings.Bitrix.WebhookUrl);
            Assert.Equal(Path.Combine(dir, "leadflow.db"), settings.DatabasePath);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task SaveAsync_Then_LoadAsync_PreservesMonitoringSafetyWithinNormalizeRules()
    {
        var dir = CreateTempDataDir();
        try
        {
            var sut = new JsonSettingsService(dir);
            var first = await sut.LoadAsync(CancellationToken.None);
            first.MonitoringSafety.CycleDelayMinMinutes = 4;
            first.MonitoringSafety.CycleDelayMaxMinutes = 20;
            await sut.SaveAsync(first, CancellationToken.None);

            var sut2 = new JsonSettingsService(dir);
            var second = await sut2.LoadAsync(CancellationToken.None);

            Assert.Equal(4, second.MonitoringSafety.CycleDelayMinMinutes);
            Assert.Equal(20, second.MonitoringSafety.CycleDelayMaxMinutes);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task EnsureDatabaseEncryptionKeyAsync_GeneratesAndPersistsOnce()
    {
        var dir = CreateTempDataDir();
        try
        {
            var sut = new JsonSettingsService(dir);
            var settings = await sut.LoadAsync(CancellationToken.None);
            Assert.True(string.IsNullOrWhiteSpace(settings.DatabaseEncryptionKey));

            await sut.EnsureDatabaseEncryptionKeyAsync(settings, CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(settings.DatabaseEncryptionKey));
            var keyAfterFirst = settings.DatabaseEncryptionKey;

            var sut2 = new JsonSettingsService(dir);
            var reloaded = await sut2.LoadAsync(CancellationToken.None);
            Assert.Equal(keyAfterFirst, reloaded.DatabaseEncryptionKey);

            await sut2.EnsureDatabaseEncryptionKeyAsync(reloaded, CancellationToken.None);
            Assert.Equal(keyAfterFirst, reloaded.DatabaseEncryptionKey);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    private static string CreateTempDataDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "LeadFlow_SettingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup for temp tests
        }
    }
}

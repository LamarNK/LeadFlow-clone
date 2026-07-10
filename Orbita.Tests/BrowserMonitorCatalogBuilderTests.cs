using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BrowserMonitorCatalogBuilderTests
{
    [Fact]
    public void BuildActiveCatalog_WithNoRegistrations_ReturnsEmptyList()
    {
        var catalog = BrowserMonitorCatalogBuilder.BuildActiveCatalog(
            [],
            new Dictionary<Guid, long>(),
            new Dictionary<Guid, (string? SubProfileId, string? SubProfileName, string? PageUrl)>());

        Assert.Empty(catalog);
    }

    [Fact]
    public void BuildActiveCatalog_WithTwoRegistrations_ReturnsRunningBrowsersWithIndexes()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var registrations = new[]
        {
            new BrowserMonitorCatalogBuilder.ActiveBrowserSeed(firstId, "Account A", "profile-a"),
            new BrowserMonitorCatalogBuilder.ActiveBrowserSeed(secondId, "Account B", "profile-b")
        };

        var catalog = BrowserMonitorCatalogBuilder.BuildActiveCatalog(
            registrations,
            new Dictionary<Guid, long>(),
            new Dictionary<Guid, (string? SubProfileId, string? SubProfileName, string? PageUrl)>());

        Assert.Equal(2, catalog.Count);
        Assert.All(catalog, browser => Assert.Equal(BrowserMonitorStatuses.Running, browser.Status));
        Assert.Equal([1, 2], catalog.Select(browser => browser.Index).ToArray());
        Assert.Equal("Account A", catalog[0].AccountName);
        Assert.Equal("profile-b", catalog[1].AdsPowerProfileId);
    }

    [Fact]
    public void BuildActiveCatalog_UsesRuntimeMetadataAndLastFrameTimestamp()
    {
        var accountId = Guid.NewGuid();
        var lastFrameAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var registrations = new[]
        {
            new BrowserMonitorCatalogBuilder.ActiveBrowserSeed(accountId, "Account A", "profile-a")
        };

        var catalog = BrowserMonitorCatalogBuilder.BuildActiveCatalog(
            registrations,
            new Dictionary<Guid, long> { [accountId] = lastFrameAtMs },
            new Dictionary<Guid, (string? SubProfileId, string? SubProfileName, string? PageUrl)>
            {
                [accountId] = ("sub-1", "Sub profile", "https://avito.ru/profile")
            });

        var browser = Assert.Single(catalog);
        Assert.Equal("https://avito.ru/profile", browser.PageUrl);
        Assert.Equal("sub-1", browser.SubProfileId);
        Assert.Equal("Sub profile", browser.SubProfileName);
        Assert.Equal(lastFrameAtMs, browser.LastFrameAtMs);
    }
}
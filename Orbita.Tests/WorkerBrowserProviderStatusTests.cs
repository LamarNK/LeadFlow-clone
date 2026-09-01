using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerBrowserProviderStatusTests
{
    [Theory]
    [InlineData(false, false, false, true, WorkerBrowserProviderStatus.Disabled, "Выключен")]
    [InlineData(false, true, true, true, WorkerBrowserProviderStatus.Disabled, "Выключен")]
    [InlineData(true, false, true, true, WorkerBrowserProviderStatus.Checking, "Проверяется")]
    [InlineData(true, true, false, true, WorkerBrowserProviderStatus.NeedsSetup, "Требуется настройка")]
    [InlineData(true, false, false, null, WorkerBrowserProviderStatus.Unchecked, "Не проверено")]
    [InlineData(true, false, false, false, WorkerBrowserProviderStatus.Error, "Ошибка подключения")]
    [InlineData(true, false, false, true, WorkerBrowserProviderStatus.Connected, "Подключён")]
    public void Resolve_UsesStrictPriority(
        bool enabled,
        bool needsSetup,
        bool checking,
        bool? lastSucceeded,
        string expectedStatus,
        string expectedLabel)
    {
        var status = WorkerBrowserProviderStatus.Resolve(enabled, needsSetup, checking, lastSucceeded);
        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedLabel, WorkerBrowserProviderStatus.Label(status));
    }

    [Fact]
    public void Resolve_NeverReturnsConnected_WhenLastCheckMissingOrFailed()
    {
        Assert.NotEqual(
            WorkerBrowserProviderStatus.Connected,
            WorkerBrowserProviderStatus.Resolve(true, false, false, lastSucceeded: null));
        Assert.NotEqual(
            WorkerBrowserProviderStatus.Connected,
            WorkerBrowserProviderStatus.Resolve(true, false, false, lastSucceeded: false));
        Assert.NotEqual(
            WorkerBrowserProviderStatus.Connected,
            WorkerBrowserProviderStatus.Resolve(false, false, false, lastSucceeded: true));
        Assert.Equal(
            WorkerBrowserProviderStatus.Connected,
            WorkerBrowserProviderStatus.Resolve(true, false, false, lastSucceeded: true));
    }

    [Theory]
    [InlineData("AdsPower", WorkerBrowserProviderKinds.AdsPower)]
    [InlineData("adspower", WorkerBrowserProviderKinds.AdsPower)]
    [InlineData("Multilogin", WorkerBrowserProviderKinds.Multilogin)]
    [InlineData("mlx", WorkerBrowserProviderKinds.Multilogin)]
    [InlineData("Local", WorkerBrowserProviderKinds.Local)]
    [InlineData("chrome", WorkerBrowserProviderKinds.Local)]
    [InlineData("localChrome", WorkerBrowserProviderKinds.Local)]
    public void Normalize_AcceptsKnownAliases(string raw, string expected)
    {
        Assert.Equal(expected, WorkerBrowserProviderKinds.Normalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bitrix")]
    [InlineData("puppeteer")]
    public void Normalize_RejectsUnknown(string? raw)
    {
        Assert.Null(WorkerBrowserProviderKinds.Normalize(raw));
    }

    [Fact]
    public void SupportsCatalogSync_OnlyAdsPowerAndMultilogin()
    {
        Assert.True(WorkerBrowserProviderKinds.SupportsCatalogSync(WorkerBrowserProviderKinds.AdsPower));
        Assert.True(WorkerBrowserProviderKinds.SupportsCatalogSync(WorkerBrowserProviderKinds.Multilogin));
        Assert.False(WorkerBrowserProviderKinds.SupportsCatalogSync(WorkerBrowserProviderKinds.Local));
    }
}

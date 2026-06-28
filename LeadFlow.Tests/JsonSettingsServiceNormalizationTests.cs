using LeadFlow.Core.Models;
using LeadFlow.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class JsonSettingsServiceNormalizationTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 30)]
    [InlineData(120, 120)]
    [InlineData(3600, 3600)]
    [InlineData(7200, 3600)]
    public void Normalize_ClampsCheckIntervalSeconds(int input, int expected)
    {
        var settings = NewSettings();
        settings.MonitoringSafety.CheckIntervalSeconds = input;

        JsonSettingsService.NormalizeSettings(settings);

        Assert.Equal(expected, settings.MonitoringSafety.CheckIntervalSeconds);
    }

    [Fact]
    public void Normalize_TrimsBitrixWebhookUrl()
    {
        var settings = NewSettings();
        settings.Bitrix.WebhookUrl = "  https://example.bitrix24.ru/rest/1/x/  ";

        JsonSettingsService.NormalizeSettings(settings);

        Assert.Equal("https://example.bitrix24.ru/rest/1/x/", settings.Bitrix.WebhookUrl);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(25, 10)]
    public void Normalize_ClampsMaxConcurrentAccounts(int input, int expected)
    {
        var settings = NewSettings();
        settings.MonitoringSafety.MaxConcurrentAccounts = input;

        JsonSettingsService.NormalizeSettings(settings);

        Assert.Equal(expected, settings.MonitoringSafety.MaxConcurrentAccounts);
    }

    [Fact]
    public void Normalize_ForcesDemoModeOff()
    {
        var settings = NewSettings();
        settings.DemoModeEnabled = true;

        JsonSettingsService.NormalizeSettings(settings);

        Assert.False(settings.DemoModeEnabled);
    }

    [Fact]
    public void Normalize_ResetsAvitoSelectorsAndKeepsContainerInstances()
    {
        var settings = new AppSettings
        {
            MonitoringSafety = null!,
            Bitrix = null!,
            AvitoSelectors = null!,
            Avito = null!
        };

        JsonSettingsService.NormalizeSettings(settings);

        Assert.NotNull(settings.MonitoringSafety);
        Assert.NotNull(settings.Bitrix);
        Assert.NotNull(settings.AvitoSelectors);
        Assert.NotNull(settings.Avito);
    }

    private static AppSettings NewSettings() => new()
    {
        MonitoringSafety = new MonitoringSafetyOptions(),
        Bitrix = new BitrixSettings(),
        AvitoSelectors = new AvitoSelectorOptions(),
        Avito = new AvitoSettings()
    };
}

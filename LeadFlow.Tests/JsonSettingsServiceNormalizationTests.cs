using LeadFlow.Models;
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
    public void Normalize_AppliesCycleDelayBoundsRules()
    {
        var settings = NewSettings();
        settings.MonitoringSafety.CycleDelayMinMinutes = 200;
        settings.MonitoringSafety.CycleDelayMaxMinutes = 0;

        JsonSettingsService.NormalizeSettings(settings);

        Assert.Equal(MonitoringCycleDelay.MinAllowedMinutes, settings.MonitoringSafety.CycleDelayMinMinutes);
        Assert.Equal(MonitoringCycleDelay.MaxAllowedMinutes, settings.MonitoringSafety.CycleDelayMaxMinutes);
    }

    [Theory]
    [InlineData(0, 45)]
    [InlineData(4, 45)]
    [InlineData(5, 5)]
    [InlineData(45, 45)]
    [InlineData(240, 240)]
    [InlineData(241, 45)]
    public void Normalize_ClampsActiveAdsRefreshInterval(int input, int expected)
    {
        var settings = NewSettings();
        settings.MonitoringSafety.ActiveAdsRefreshIntervalMinutes = input;

        JsonSettingsService.NormalizeSettings(settings);

        Assert.Equal(expected, settings.MonitoringSafety.ActiveAdsRefreshIntervalMinutes);
    }

    [Fact]
    public void Normalize_AlwaysSetsFixedBitrixWebhookUrl()
    {
        var settings = NewSettings();
        settings.Bitrix.WebhookUrl = "https://malicious.example/";

        JsonSettingsService.NormalizeSettings(settings);

        Assert.Equal(JsonSettingsService.FixedBitrixWebhookUrl, settings.Bitrix.WebhookUrl);
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

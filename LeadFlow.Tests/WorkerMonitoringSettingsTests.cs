using System.Reflection;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;
using Xunit;

namespace LeadFlow.Tests;

public sealed class WorkerMonitoringSettingsTests
{
    [Fact]
    public void SelectRunnableAccounts_IncludesAdsPowerAndMultilogin_AndSkipsDisabledOrLegacy()
    {
        var adsPower = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            ProfileProvider = AvitoProfileProvider.AdsPower,
            AdsPowerProfileId = "ads-profile",
            AdsPowerApiBaseUrl = "http://local.adspower.net:50325"
        };
        var multilogin = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            ProfileProvider = AvitoProfileProvider.Multilogin,
            MultiloginProfileId = "mlx-profile",
            MultiloginFolderId = "mlx-folder",
            MultiloginLauncherUrl = "https://launcher.mlx.yt:45001",
            MultiloginAutomationToken = "test-token"
        };
        var disabledMultilogin = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            IsEnabled = false,
            ProfileProvider = AvitoProfileProvider.Multilogin,
            MultiloginProfileId = "disabled-profile"
        };
        var local = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            ProfileProvider = AvitoProfileProvider.Local,
            BrowserProfilePath = @"D:\Orbita\ChromeProfiles\acc-1"
        };
        var incompleteLocal = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            ProfileProvider = AvitoProfileProvider.Local
        };
        var legacy = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            ProfileProvider = AvitoProfileProvider.Local
        };

        var selected = SelectRunnableAccounts(
            [adsPower, multilogin, disabledMultilogin, local, incompleteLocal, legacy]);

        Assert.Equal([adsPower.Id, multilogin.Id, local.Id], selected.Select(static account => account.Id));
    }

    [Fact]
    public void SelectRunnableAccounts_SkipsDisabledBrowserProviders()
    {
        var adsPower = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            ProfileProvider = AvitoProfileProvider.AdsPower,
            AdsPowerProfileId = "ads-profile",
            AdsPowerApiBaseUrl = "http://local.adspower.net:50325"
        };
        var multilogin = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            ProfileProvider = AvitoProfileProvider.Multilogin,
            MultiloginProfileId = "mlx-profile",
            MultiloginFolderId = "mlx-folder",
            MultiloginLauncherUrl = "https://launcher.mlx.yt:45001",
            MultiloginAutomationToken = "test-token"
        };
        var local = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            ProfileProvider = AvitoProfileProvider.Local,
            BrowserProfilePath = @"D:\Orbita\ChromeProfiles\acc-1"
        };

        var selected = SelectRunnableAccounts(
            [adsPower, multilogin, local],
            adsPowerEnabled: false,
            multiloginEnabled: true,
            localChromeEnabled: false);

        Assert.Equal([multilogin.Id], selected.Select(static account => account.Id));
    }

    [Fact]
    public void SelectRunnableAccounts_AllProvidersOff_ReturnsEmpty()
    {
        var adsPower = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            ProfileProvider = AvitoProfileProvider.AdsPower,
            AdsPowerProfileId = "ads-profile",
            AdsPowerApiBaseUrl = "http://local.adspower.net:50325"
        };
        var local = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            ProfileProvider = AvitoProfileProvider.Local,
            BrowserProfilePath = @"D:\Orbita\ChromeProfiles\acc-1"
        };

        var selected = SelectRunnableAccounts(
            [adsPower, local],
            adsPowerEnabled: false,
            multiloginEnabled: false,
            localChromeEnabled: false);

        Assert.Empty(selected);
    }

    [Fact]
    public void ToAppSettings_AutoReplyDisabledInOrbit_DisablesAutoReply()
    {
        var settings = MapSettings(new WorkerMonitoringConfig());

        Assert.False(settings.Avito.MessengerAutoReply.Enabled);
    }

    [Fact]
    public void ToAppSettings_AutoReplyEnabledInOrbit_UsesOrbitSettings()
    {
        var settings = MapSettings(new WorkerMonitoringConfig
        {
            MessengerAutoReply = new AvitoMessengerAutoReplySettings
            {
                Enabled = true,
                Message = "Напишем вам позже."
            }
        });

        Assert.True(settings.Avito.MessengerAutoReply.Enabled);
        Assert.Equal("Напишем вам позже.", settings.Avito.MessengerAutoReply.Message);
    }

    private static AppSettings MapSettings(WorkerMonitoringConfig config)
    {
        var mapper = typeof(WorkerMonitoringService).GetMethod(
            "ToAppSettings",
            BindingFlags.NonPublic | BindingFlags.Static);

        return Assert.IsType<AppSettings>(mapper?.Invoke(null, [config]));
    }

    private static List<AvitoAccount> SelectRunnableAccounts(
        IEnumerable<AvitoAccount> accounts,
        bool adsPowerEnabled = true,
        bool multiloginEnabled = true,
        bool localChromeEnabled = true)
    {
        var selector = typeof(WorkerMonitoringService).GetMethod(
            "SelectRunnableAccounts",
            BindingFlags.NonPublic | BindingFlags.Static);

        return Assert.IsType<List<AvitoAccount>>(selector?.Invoke(null, [
            new WorkerMonitoringConfig
            {
                Accounts = accounts as IReadOnlyList<AvitoAccount> ?? accounts.ToList(),
                AdsPowerEnabled = adsPowerEnabled,
                MultiloginEnabled = multiloginEnabled,
                LocalChromeEnabled = localChromeEnabled
            }
        ]));
    }
}

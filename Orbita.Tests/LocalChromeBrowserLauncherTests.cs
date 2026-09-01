using LeadFlow.Core.Services.LocalChrome;

namespace Orbita.Tests;

public sealed class LocalChromeBrowserLauncherTests
{
    [Fact]
    public void ResolveLaunchArgs_WithoutProxy_ReturnsEmptyArrayNotNull()
    {
        var options = new LocalChromeLaunchOptions
        {
            UserDataDir = @"D:\Orbita\ChromeProfiles\acc-1",
            ExecutablePath = @"C:\Chrome\chrome.exe",
            ProxyEnabled = false
        };

        Assert.Null(options.ChromiumArgs);
        var args = LocalChromeBrowserLauncher.ResolveLaunchArgs(options);
        Assert.NotNull(args);
        Assert.Empty(args);
    }

    [Fact]
    public void ResolveLaunchArgs_WithProxy_PassesOnlyHttpProxyServerArg()
    {
        var options = new LocalChromeLaunchOptions
        {
            UserDataDir = @"D:\Orbita\ChromeProfiles\acc-1",
            ExecutablePath = @"C:\Chrome\chrome.exe",
            ProxyEnabled = true,
            ProxyServer = "203.0.113.10:8080",
            ProxyUsername = "px-user",
            ProxyPassword = "proxy-secret"
        };

        var args = LocalChromeBrowserLauncher.ResolveLaunchArgs(options);
        Assert.Equal(["--proxy-server=http://203.0.113.10:8080"], args);
        Assert.DoesNotContain("px-user", args[0], StringComparison.Ordinal);
        Assert.DoesNotContain("proxy-secret", args[0], StringComparison.Ordinal);
        Assert.DoesNotContain("@", args[0], StringComparison.Ordinal);
    }
}

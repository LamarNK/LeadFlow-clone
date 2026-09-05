using LeadFlow.Core.Services.LocalChrome;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class LocalChromeLaunchDiagnosticsTests
{
    [Fact]
    public void ToSafeError_KeepsReason_AndStripsSecretsAndUserInfo()
    {
        const string password = "proxy-secret";
        const string avitoPassword = "avito-pass-1";
        var inner = new TimeoutException(
            "Timed out after waiting for Chrome. DevToolsActivePort file doesn't exist. user:proxy-secret@203.0.113.10:8080 avito-pass-1");
        var ex = new InvalidOperationException("Failed to launch browser!", inner);

        var message = LocalChromeLaunchDiagnostics.ToSafeError(
            ex,
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"D:\Orbita\ChromeProfiles\acc-1",
            proxyEnabled: true,
            "px-user",
            password,
            avitoPassword);

        Assert.StartsWith(LocalChromeLaunchDiagnostics.FailurePrefix, message, StringComparison.Ordinal);
        Assert.Contains("Failed to launch browser!", message, StringComparison.Ordinal);
        Assert.Contains("DevToolsActivePort", message, StringComparison.Ordinal);
        Assert.Contains(LocalChromeLaunchDiagnostics.WaitDevToolsStage, message, StringComparison.Ordinal);
        Assert.Contains("TimeoutException", message, StringComparison.Ordinal);
        Assert.Contains(@"C:\Program Files\Google\Chrome\Application\chrome.exe", message, StringComparison.Ordinal);
        Assert.Contains(@"D:\Orbita\ChromeProfiles\acc-1", message, StringComparison.Ordinal);
        Assert.Contains("proxy=true", message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, message, StringComparison.Ordinal);
        Assert.DoesNotContain(avitoPassword, message, StringComparison.Ordinal);
        Assert.DoesNotContain("px-user", message, StringComparison.Ordinal);
        Assert.DoesNotContain("user:proxy-secret@", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Не удалось открыть обычный браузер.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToSafeError_StripsBearerAndApiKey()
    {
        const string apiKey = "ads-power-api-key-9f3a";
        var ex = new InvalidOperationException(
            $"Failed to launch browser! Authorization: Bearer {apiKey} token={apiKey}");

        var message = LocalChromeLaunchDiagnostics.ToSafeError(
            ex,
            @"C:\Chrome\chrome.exe",
            @"D:\Orbita\ChromeProfiles\acc-1",
            proxyEnabled: false,
            apiKey);

        Assert.Contains("Failed to launch browser!", message, StringComparison.Ordinal);
        Assert.Contains("proxy=false", message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, message, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wrap_PreservesInnerException_AndDoesNotChangeChromiumArgs()
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
        var inner = new InvalidOperationException("Failed to launch browser! user:proxy-secret@203.0.113.10:8080");

        var wrapped = LocalChromeLaunchDiagnostics.Wrap(
            inner,
            options.ExecutablePath,
            options.UserDataDir,
            options.ProxyEnabled,
            options.ProxyUsername,
            options.ProxyPassword);

        Assert.Same(inner, wrapped.InnerException);
        Assert.StartsWith(LocalChromeLaunchDiagnostics.FailurePrefix, wrapped.Message, StringComparison.Ordinal);
        Assert.Contains("Failed to launch browser!", wrapped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy-secret", wrapped.Message, StringComparison.Ordinal);
        Assert.Equal(["--proxy-server=http://203.0.113.10:8080"], options.ChromiumArgs);
        Assert.Equal(
            ["--proxy-server=http://203.0.113.10:8080"],
            LocalChromeProxyRules.ToChromiumArgs(true, "203.0.113.10:8080"));
        Assert.Null(LocalChromeProxyRules.ToChromiumArgs(false, "203.0.113.10:8080"));
        Assert.DoesNotContain("px-user", options.ChromiumArgs![0], StringComparison.Ordinal);
        Assert.DoesNotContain("@", options.ChromiumArgs[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Failed to launch browser!", "запуск процесса")]
    [InlineData("The browser is already running for C:\\Orbita\\ChromeProfiles\\acc. Use a different UserDataDir", "профиль занят")]
    [InlineData("Failed to create a ProcessSingleton for your profile directory", "профиль занят")]
    [InlineData("Timeout exceeded while waiting for the browser. DevToolsActivePort", "ожидание DevTools")]
    [InlineData("WebSocket failed to connect to ws://127.0.0.1:9222", "подключение")]
    public void ClassifyStage_UsesExceptionText(string message, string expected)
    {
        Assert.Equal(expected, LocalChromeLaunchDiagnostics.ClassifyStage(new InvalidOperationException(message)));
    }

    [Fact]
    public void IsProfileBusy_DetectsSingletonMessage()
    {
        var ex = new InvalidOperationException(
            "The browser is already running for C:\\Users\\Admin\\AppData\\Local\\Orbita\\ChromeProfiles\\acc");
        Assert.True(LocalChromeLaunchDiagnostics.IsProfileBusy(ex));
        Assert.True(LocalChromeLaunchDiagnostics.IsProfileBusy(ex.Message));
        Assert.False(LocalChromeLaunchDiagnostics.IsProfileBusy("DevToolsActivePort file doesn't exist"));
    }

    [Fact]
    public void ErrorKey_IsLocalChromeLaunch()
    {
        Assert.Equal("local.chrome.launch", LocalChromeLaunchDiagnostics.ErrorKey);
    }
}

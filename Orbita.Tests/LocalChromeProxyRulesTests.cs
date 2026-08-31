using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class LocalChromeProxyRulesTests
{
    [Theory]
    [InlineData("203.0.113.10:8080", "203.0.113.10:8080")]
    [InlineData("proxy.example.com:3128", "proxy.example.com:3128")]
    [InlineData("localhost:1", "localhost:1")]
    [InlineData("[2001:db8::1]:8080", "[2001:db8::1]:8080")]
    public void TryNormalizeAddress_AcceptsHostPort(string input, string expected)
    {
        Assert.True(LocalChromeProxyRules.TryNormalizeAddress(input, out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("http://203.0.113.10:8080")]
    [InlineData("https://203.0.113.10:8080")]
    [InlineData("user:pass@203.0.113.10:8080")]
    [InlineData("203.0.113.10:8080/path")]
    [InlineData("203.0.113.10")]
    [InlineData("203.0.113.10:0")]
    [InlineData("203.0.113.10:65536")]
    [InlineData("203.0.113.10: 8080")]
    [InlineData("socks5://203.0.113.10:1080")]
    public void TryNormalizeAddress_RejectsBadValues(string input)
    {
        Assert.False(LocalChromeProxyRules.TryNormalizeAddress(input, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("pass", error ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void TryNormalizeAddress_RejectsEmbeddedCredentials()
    {
        Assert.False(LocalChromeProxyRules.TryNormalizeAddress("user:secret@host:8080", out _, out var error));
        Assert.DoesNotContain("secret", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ToChromiumArgs_OnlyWhenEnabledAndValid()
    {
        Assert.Equal(
            ["--proxy-server=http://203.0.113.10:8080"],
            LocalChromeProxyRules.ToChromiumArgs(true, "203.0.113.10:8080"));
        Assert.Null(LocalChromeProxyRules.ToChromiumArgs(false, "203.0.113.10:8080"));
        Assert.Null(LocalChromeProxyRules.ToChromiumArgs(true, "http://203.0.113.10:8080"));
        Assert.Null(LocalChromeProxyRules.ToChromiumArgs(true, "user:pass@203.0.113.10:8080"));
        var args = LocalChromeProxyRules.ToChromiumArgs(true, "203.0.113.10:8080");
        Assert.DoesNotContain("user", string.Join(' ', args!), StringComparison.Ordinal);
        Assert.DoesNotContain("@", string.Join(' ', args!), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_AndBrowserSession_UseFixedLabels()
    {
        Assert.Equal(LocalChromeProxyRules.StatusNotConfigured, LocalChromeProxyRules.Status(false, "203.0.113.10:8080"));
        Assert.Equal(LocalChromeProxyRules.StatusConfigured, LocalChromeProxyRules.Status(true, "203.0.113.10:8080"));
        Assert.Equal(LocalChromeProxyRules.StatusCheckFailed, LocalChromeProxyRules.Status(true, "http://bad"));
        Assert.Equal(LocalChromeProxyRules.BrowserMonitoring, LocalChromeProxyRules.BrowserSessionStatus(true, true));
        Assert.Equal(LocalChromeProxyRules.BrowserOpenedManually, LocalChromeProxyRules.BrowserSessionStatus(false, true));
        Assert.Equal(LocalChromeProxyRules.BrowserFree, LocalChromeProxyRules.BrowserSessionStatus(false, false));
        Assert.False(LocalChromeProxyRules.CanOpenBrowser(true, true, isMonitoring: true));
        Assert.True(LocalChromeProxyRules.CanOpenBrowser(true, true, isMonitoring: false));
        Assert.False(LocalChromeProxyRules.CanOpenBrowser(false, true, isMonitoring: false));
    }

    [Fact]
    public void SanitizeError_StripsPasswordAndUserinfo()
    {
        var sanitized = LocalChromeProxyRules.SanitizeError(
            "proxy http://user:secret@203.0.113.10:8080 failed secret",
            "user",
            "secret");
        Assert.DoesNotContain("secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("user:secret@", sanitized, StringComparison.Ordinal);
    }
}

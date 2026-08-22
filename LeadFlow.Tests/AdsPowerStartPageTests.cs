using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AdsPowerStartPageTests
{
    private const string FailHtml = """
        <div class="_header__proxy__fail_clmtb_70">
          <div class="_header__ip_clmtb_60">Proxy failure</div>
        </div>
        <div class="_header__proxy__fail_text_clmtb_104">
          <p>1. Please check if your network meets the proxy service provider's conditions.</p>
        </div>
        """;

    [Theory]
    [InlineData("https://start.adspower.net/?id=k1dp9we7&host=127.0.0.1:20725")]
    [InlineData("https://start.adspower.com/?id=abc")]
    [InlineData("http://start.adspower.net/")]
    public void IsUrl_RecognizesAdsPowerStartPage(string url)
    {
        Assert.True(AdsPowerStartPage.IsUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("about:blank")]
    [InlineData("https://www.avito.ru/profile/pro/items")]
    [InlineData("https://adspower.net/start")]
    public void IsUrl_RejectsOtherPages(string? url)
    {
        Assert.False(AdsPowerStartPage.IsUrl(url));
    }

    [Fact]
    public void Parse_DetectsProxyFailureFromStartPageHtml()
    {
        Assert.Equal(AdsPowerStartPageProxyStatus.Failed, AdsPowerStartPage.Parse(FailHtml));
    }

    [Fact]
    public void Parse_DetectsChineseProxyFailure()
    {
        Assert.Equal(
            AdsPowerStartPageProxyStatus.Failed,
            AdsPowerStartPage.Parse("<div>代理失败</div>"));
    }

    [Fact]
    public void Parse_DetectsSuccessWhenPublicIpIsShown()
    {
        Assert.Equal(
            AdsPowerStartPageProxyStatus.Ok,
            AdsPowerStartPage.Parse("""
                <div class="_header__ip_clmtb_60">109.94.172.89</div>
                <div>Kazakhstan · Almaty</div>
                """));
    }

    [Fact]
    public void Parse_IgnoresLoopbackIpFromStartUrl()
    {
        Assert.Equal(
            AdsPowerStartPageProxyStatus.Unknown,
            AdsPowerStartPage.Parse("https://start.adspower.net/?id=k1dp9we7&host=127.0.0.1:20725"));
    }

    [Fact]
    public void Parse_FailureWinsOverIpInSameDocument()
    {
        Assert.Equal(
            AdsPowerStartPageProxyStatus.Failed,
            AdsPowerStartPage.Parse(FailHtml + " 8.8.8.8"));
    }

    [Fact]
    public void ShouldKeepWaitingForStartupNavigation_StopsOnceStartPageLoaded()
    {
        Assert.False(AdsPowerAvitoAutomationService.ShouldKeepWaitingForStartupNavigation(
            ["https://start.adspower.net/?id=k1dp9we7&host=127.0.0.1:20725"],
            elapsed: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public void IsRetryableAdsPowerStartupFailure_DoesNotRetryDeadProxy()
    {
        Assert.False(AdsPowerAvitoAutomationService.IsRetryableAdsPowerStartupFailure(
            new AdsPowerProxyFailureException("https://start.adspower.net/?id=k1dp9we7")));
    }

    [Fact]
    public void LooksLikeMessage_RecognizesProxyErrorText()
    {
        var ex = new AdsPowerProxyFailureException("https://start.adspower.net/?id=k1dp9we7");
        Assert.True(AdsPowerProxyFailureException.LooksLikeMessage(ex.Message));
        Assert.Contains("прокси не работает", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatIssue_UsesProxyFailureLabel()
    {
        var account = new AvitoAccount { DisplayName = "Авито 44" };
        var message = AccountIssueFormatting.FormatIssue(
            account,
            null,
            AvitoSubProfileIssueKind.ProxyFailure,
            AdsPowerProxyFailureException.UserDetail);

        Assert.Contains("прокси не работает", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Авито 44", message);
    }
}

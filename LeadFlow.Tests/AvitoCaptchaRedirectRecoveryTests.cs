using LeadFlow.Core.Services.Captcha;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoCaptchaRedirectRecoveryTests
{
    [Fact]
    public void RequiresRecovery_VerificationPassedButRedirecting_ReturnsTrue()
    {
        const string html = """
            <section class="firewall-container">
              <h2>Доступ ограничен: проблема с IP</h2>
              <p class="status success">Проверка пройдена, перенаправление…</p>
            </section>
            """;

        Assert.True(AvitoCaptchaRedirectRecovery.RequiresRecovery(html));
        Assert.False(AvitoGeeTestSolveSupport.ShouldCreateProviderTask(html));
    }

    [Theory]
    [InlineData(1, AvitoCaptchaRecoveryAction.NavigateCurrentPage)]
    [InlineData(2, AvitoCaptchaRecoveryAction.Reload)]
    [InlineData(3, AvitoCaptchaRecoveryAction.NavigateProfileItems)]
    public void GetAction_UsesBoundedProgressiveRecovery(
        int attempt,
        AvitoCaptchaRecoveryAction expected)
    {
        Assert.Equal(expected, AvitoCaptchaRedirectRecovery.GetAction(attempt));
    }

    [Fact]
    public void GetAction_AfterRecoveryBudget_ReturnsNone()
    {
        Assert.Equal(AvitoCaptchaRecoveryAction.None, AvitoCaptchaRedirectRecovery.GetAction(4));
    }

    [Fact]
    public void GetCurrentPageTarget_RemovesSpaHashBeforeNavigation()
    {
        var target = AvitoCaptchaRedirectRecovery.GetCurrentPageTarget(
            "https://www.avito.ru/profile/dashboard#profile/switch?withEntities=true");

        Assert.Equal("https://www.avito.ru/profile/dashboard", target);
    }
}

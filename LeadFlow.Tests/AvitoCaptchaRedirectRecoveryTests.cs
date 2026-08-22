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

    [Fact]
    public void RequiresRecovery_RealFirewallSuccessOverlay_ReturnsTrue()
    {
        const string html = """
            <div id="geetest_captcha" style="display: none">
              <p class="text">
                Иногда такое случается, чтобы вернуться на сайт <b>нажмите на кнопку Продолжить</b> для решения капчи
              </p>
              <script src="https://www.avito.st/s/captcha/gt4.js" async="" defer=""></script>
              <input type="hidden" name="captcha-response">
            </div>
            <div class="form-action"><p style="color: green; font-size: 16px; font-weight: 700;">Проверка пройдена, перенаправление...</p></div>
            <p class="text firewall-p-bold">Что можно сделать, если проблема повторяется</p>
            <p class="text">— Отключить VPN.</p>
            """;

        Assert.True(AvitoCaptchaRedirectRecovery.RequiresRecovery(html));
        Assert.False(AvitoGeeTestSolveSupport.ShouldCreateProviderTask(html));
    }

    [Fact]
    public void RequiresRecovery_ClassicFirewallScriptContainsRedirectTemplate_IsStillLiveChallenge()
    {
        // Реальный HTML Avito: зелёная фраза живёт в <script> как шаблон showRedirectMessage,
        // а на экране кнопка «Продолжить». GetContentAsync отдаёт скрипт целиком —
        // солвер не должен принимать это за залипшее «перенаправление».
        const string html = """
            <div class="firewall-container">
              <h2 class="firewall-title">Доступ ограничен: проблема с IP</h2>
              <form class="form js-submit js-firewall-form" autocomplete="off">
                <div id="geetest_captcha" style="display: inline-block;">
                  <p class="text">нажмите на кнопку Продолжить для решения капчи</p>
                  <input type="hidden" name="captcha-response">
                </div>
                <div class="form-action">
                  <button class="button" type="button" name="submit">Продолжить</button>
                </div>
              </form>
            </div>
            <script>
              function showRedirectMessage() {
                const formAction = document.querySelector('.form-action');
                if (formAction) {
                  formAction.innerHTML = '<p style="color: green; font-size: 16px; font-weight: 700;">Проверка пройдена, перенаправление...</p>';
                }
              }
            </script>
            """;

        Assert.False(AvitoCaptchaRedirectRecovery.RequiresRecovery(html));
        Assert.True(AvitoGeeTestSolveSupport.ShouldCreateProviderTask(html));
    }

    [Fact]
    public void RequiresRecovery_UnsolvedContinuePrompt_ReturnsFalse()
    {
        const string html = """
            <div class="firewall-container">
              <h2>Доступ ограничен: проблема с IP</h2>
              <div id="geetest_captcha">
                <p class="text">нажмите на кнопку Продолжить для решения капчи</p>
                <button type="submit">Продолжить</button>
              </div>
            </div>
            """;

        Assert.False(AvitoCaptchaRedirectRecovery.RequiresRecovery(html));
        Assert.True(AvitoGeeTestSolveSupport.ShouldCreateProviderTask(html));
    }

    [Theory]
    [InlineData(1, AvitoCaptchaRecoveryAction.Reload)]
    [InlineData(2, AvitoCaptchaRecoveryAction.NavigateCurrentPage)]
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

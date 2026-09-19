using LeadFlow.Core.Services.Avito.Session;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoPageObstacleScriptTests
{
    [Fact]
    public void ProbeScript_ReadsVisibleCaptchaDialogDirectly_NotFromSlicedBodyText()
    {
        var script = AvitoPageObstacleScripts.BuildProbeScript();

        // Модалка капчи читается напрямую из узла диалога (innerText узла) —
        // на странице откликов текст модалки в конце DOM не попадает в срез body.innerText.
        Assert.Contains("[role='dialog'][aria-modal='true']", script, StringComparison.Ordinal);
        Assert.Contains("[aria-modal='true']", script, StringComparison.Ordinal);
        Assert.Contains("[data-scroll-lock-ignore='true']", script, StringComparison.Ordinal);
        Assert.Contains("dialog.innerText", script, StringComparison.Ordinal);
        Assert.DoesNotContain("slice(0, 12000)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeScript_DetectsGeeTestV4WithoutLegacyWidgetId()
    {
        var script = AvitoPageObstacleScripts.BuildProbeScript();

        // GeeTest v4 (gt4.js) не создаёт #geetest_captcha: ищем geetest-разметку и v4-скрипты.
        Assert.Contains("script[src*='/s/captcha/gt4']", script, StringComparison.Ordinal);
        Assert.Contains("script[src*='gcaptcha4.geetest.com']", script, StringComparison.Ordinal);
        Assert.Contains("js-geetest-captcha-script", script, StringComparison.Ordinal);
        Assert.Contains("geetest_boxShow", script, StringComparison.Ordinal);
        Assert.Contains("iframe[src*='geetest']", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeScript_MountedGeeTestScriptAloneIsNotChallenge_DialogVisibilityRequired()
    {
        var script = AvitoPageObstacleScripts.BuildProbeScript();

        // gt4.js остаётся в DOM после закрытия капчи: сигнал «скрипт смонтирован»
        // учитывается только в паре с видимым диалогом с кнопкой «Продолжить».
        Assert.Contains("hasGeeTestV4Mounted && /Продолжить/i.test(text)", script, StringComparison.Ordinal);
        Assert.Contains("isFrontmostEl(dialog)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeScript_CaptchaWorksOverNonEmptyResponsesList()
    {
        var script = AvitoPageObstacleScripts.BuildProbeScript();

        // Старые probe требовали пустой список (itemCount===0) — капча-модалка поверх
        // рабочего списка игнорировалась. Теперь диалог/виджет — самостоятельные сигналы,
        // а текстовые fallback-и ограничены пустым списком (слова из чата не считаются капчей).
        Assert.Contains("hasCaptchaDialog", script, StringComparison.Ordinal);
        Assert.Contains("hasCaptchaContinue", script, StringComparison.Ordinal);
        Assert.Contains("listMissing && hasFirewallContainer", script, StringComparison.Ordinal);
        Assert.DoesNotContain("listMissing && (hasFirewallDom || hasFirewallText)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("itemCount === 0 &&\n                statusCount === 0 &&\n                (hasFirewallDom || (hasFirewallText && hasCaptchaWidget) || hasFirewallText)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeScript_ClassifiesObstaclesAndReturnsStructuredJson()
    {
        var script = AvitoPageObstacleScripts.BuildProbeScript();

        Assert.Contains("\"manualAction\"", script, StringComparison.Ordinal);
        Assert.Contains("\"captcha\"", script, StringComparison.Ordinal);
        Assert.Contains("\"ipBlocked\"", script, StringComparison.Ordinal);
        Assert.Contains("\"loginRequired\"", script, StringComparison.Ordinal);
        Assert.Contains("\"transientError\"", script, StringComparison.Ordinal);
        Assert.Contains("captchaKind", script, StringComparison.Ordinal);
        Assert.Contains("signals", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeScript_IpBlockRequiresNoActiveCaptchaChallenge()
    {
        var script = AvitoPageObstacleScripts.BuildProbeScript();

        // Зеркалим C#-детектор: блок IP — только когда это НЕ решаемая капча.
        Assert.Contains("!hasCaptchaChallenge", script, StringComparison.Ordinal);
        Assert.Contains("hasStaticIpBlock || (hasIpText", script, StringComparison.Ordinal);
        Assert.Contains("location.hash === \"#block\"", script, StringComparison.Ordinal);
        Assert.Contains("support.avito.ru/request/720", script, StringComparison.Ordinal);
    }
}

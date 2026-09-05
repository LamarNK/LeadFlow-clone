using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Captcha;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoGeeTestSolveSupportTests
{
    [Fact]
    public void CanAutoSolve_RequiresKeyAndWidget()
    {
        const string html = """<div class="geetest_widget" data-geetest="2d9c743cf7d63dbc9db578a608196bcd"></div>""";

        Assert.False(AvitoGeeTestSolveSupport.CanAutoSolve(html, apiKey: null));
        Assert.False(AvitoGeeTestSolveSupport.CanAutoSolve(html: null, apiKey: "key"));
        Assert.True(AvitoGeeTestSolveSupport.CanAutoSolve(html, "key"));
    }

    [Fact]
    public void CaptchaPassCounters_NoteSolvedAndUnsolved_ResetPerPass()
    {
        var counters = new AvitoCaptchaPassCounters();
        using (AvitoCaptchaTaskContext.Use(options: null, counters))
        {
            AvitoCaptchaTaskContext.NoteSolved();
            AvitoCaptchaTaskContext.NoteUnsolved();
        }

        Assert.Equal(2, counters.Seen);
        Assert.Equal(1, counters.Solved);

        var snapshot = counters.SnapshotAndReset();
        Assert.Equal(2, snapshot.Seen);
        Assert.Equal(1, snapshot.Solved);
        Assert.Equal(0, counters.Seen);
        Assert.Equal(0, counters.Solved);
    }

    [Fact]
    public void MaxConcurrentGeeTestSolves_AllowsSixteenParallelProviderTasks()
    {
        Assert.Equal(16, AvitoGeeTestSolveSupport.MaxConcurrentGeeTestSolves);
    }

    [Fact]
    public void MaxGeeTestAttempts_AllowsThreeProviderTasksPerPage()
    {
        Assert.Equal(3, AvitoGeeTestSolveSupport.MaxGeeTestAttempts);
        Assert.Equal(2000, AvitoGeeTestSolveSupport.RetryDelayMs);
    }

    [Fact]
    public void ShouldCreateProviderTask_RedirectPending_ReturnsFalse()
    {
        const string html = """
            <section class="firewall-container">
              <h2>Доступ ограничен: проблема с IP</h2>
              <p class="status success">Проверка пройдена, перенаправление…</p>
            </section>
            """;

        Assert.True(AvitoCaptchaDetector.IsCaptchaHtml(html));
        Assert.True(AvitoCaptchaRedirectRecovery.RequiresRecovery(html));
        Assert.False(AvitoGeeTestSolveSupport.ShouldCreateProviderTask(html));
    }

    [Fact]
    public void ShouldCreateProviderTask_ContinueButtonCaptcha_ReturnsTrue()
    {
        const string html = """
            <div class="firewall-container">
              <h2 class="firewall-title">Доступ ограничен: проблема с IP</h2>
              <button type="submit">Продолжить</button>
            </div>
            """;

        Assert.True(AvitoGeeTestSolveSupport.ShouldCreateProviderTask(html));
        Assert.False(AvitoCaptchaDetector.HasIpBlockChallenge(html));
    }

    [Fact]
    public void CanAutoSolve_IpFirewallWithoutConfirmedGeeTest_ReturnsFalse()
    {
        // Реальная SPA-модалка Avito до нажатия «Продолжить»: тип капчи ещё не выбран.
        const string html = """
            <div data-scroll-lock-ignore="true">
              <div aria-modal="true" role="dialog">
                <h2>Доступ ограничен: проблема с IP</h2>
                <p>нажмите на кнопку Продолжить для решения капчи</p>
                <button type="submit">Продолжить</button>
              </div>
            </div>
            """;

        Assert.False(AvitoGeeTestSolveSupport.CanAutoSolve(html, "key"));
    }

    [Fact]
    public void BuildVerifyScript_ContainsAllSolutionFields()
    {
        var solution = new GeeTestV4Solution(
            CaptchaId: "2d9c743cf7d63dbc9db578a608196bcd",
            LotNumber: "lot-abc",
            PassToken: "pass-xyz",
            GenTime: "1693924478",
            CaptchaOutput: "out==");

        var script = AvitoGeeTestSolveSupport.BuildVerifyScript(solution);

        Assert.Equal("/web/3/firewallCaptcha/verify", AvitoGeeTestSolveSupport.VerifyPath);
        Assert.Contains("/web/3/firewallCaptcha/verify", script, StringComparison.Ordinal);
        Assert.Contains("X-Cube", script, StringComparison.Ordinal);
        Assert.Contains("lot-abc", script, StringComparison.Ordinal);
        Assert.Contains("pass-xyz", script, StringComparison.Ordinal);
        Assert.Contains("1693924478", script, StringComparison.Ordinal);
        Assert.Contains("out==", script, StringComparison.Ordinal);
        Assert.Contains("2d9c743cf7d63dbc9db578a608196bcd", script, StringComparison.Ordinal);
        Assert.Contains("captcha-response", script, StringComparison.Ordinal);
        Assert.Contains("Проверка пройдена, перенаправление", script, StringComparison.Ordinal);
        Assert.Contains("form-action", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHCaptchaVerifyScript_UsesHCaptchaResponseWithoutGeeTestFields()
    {
        var script = AvitoGeeTestSolveSupport.BuildHCaptchaVerifyScript(new HCaptchaSolution("hcaptcha-token"));

        Assert.Contains("/web/3/firewallCaptcha/verify", script, StringComparison.Ordinal);
        Assert.Contains("hCaptchaResponse", script, StringComparison.Ordinal);
        Assert.Contains("hcaptcha-token", script, StringComparison.Ordinal);
        Assert.DoesNotContain("lot_number", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInternalCaptchaVerifyScript_SendsRecognizedTextAsCaptcha()
    {
        var script = AvitoGeeTestSolveSupport.BuildInternalCaptchaVerifyScript(new ImageCaptchaSolution("aB72"));

        Assert.Contains("/web/3/firewallCaptcha/verify", script, StringComparison.Ordinal);
        Assert.Contains("hCaptchaResponse: ''", script, StringComparison.Ordinal);
        Assert.Contains("aB72", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NeedsContinueClick_ClassicGeetestPlaceholder_True()
    {
        const string html = """
            <div class="firewall-container">
              <div id="geetest_captcha" style="display: inline-block;">
                <p>нажмите на кнопку Продолжить для решения капчи</p>
                <script src="https://www.avito.st/s/captcha/gt4.js"></script>
                <input type="hidden" name="captcha-response">
              </div>
              <button type="button" name="submit">Продолжить</button>
            </div>
            """;

        Assert.True(AvitoGeeTestSolveSupport.NeedsContinueClick(html));
    }

    [Fact]
    public void NeedsContinueClick_FilledTokens_False()
    {
        const string html = """
            <div id="geetest_captcha">
              <input type="hidden" name="captcha-response" value="{&quot;lot_number&quot;:&quot;abc123456789&quot;}">
              <button>Продолжить</button>
            </div>
            """;

        Assert.False(AvitoGeeTestSolveSupport.NeedsContinueClick(html));
    }

    [Fact]
    public void ParseActivation_UsesOnlyTheTypeActuallyShownByAvito()
    {
        var hCaptcha = AvitoGeeTestSolveSupport.ParseActivation(
            """{"kind":"hcaptcha","clicked":true,"userAgent":"Chrome/146"}""");
        var geetest = AvitoGeeTestSolveSupport.ParseActivation(
            """{"kind":"geetest","clicked":false,"userAgent":"Chrome/146"}""");

        Assert.Equal(AvitoCaptchaKind.HCaptcha, hCaptcha.Kind);
        Assert.False(hCaptcha.IsGeeTest);
        Assert.True(hCaptcha.ContinueClicked);
        Assert.True(geetest.IsGeeTest);
    }

    [Fact]
    public void ParseActivation_UsesFirewallApiType_WhenSpaWidgetHasNotMounted()
    {
        // На SPA-модалке после клика «Продолжить» контейнер ещё может не попасть в DOM,
        // хотя /web/5/firewallCaptcha/get уже отдал выбранный сервером вид проверки.
        var activation = AvitoGeeTestSolveSupport.ParseActivation(
            """{"kind":"unknown","serverKind":"hcaptcha","siteKey":"070db171-ddb9-4c93-b7f6-d25d3c9d7e28","clicked":true} """);

        Assert.Equal(AvitoCaptchaKind.HCaptcha, activation.Kind);
        Assert.True(activation.ContinueClicked);
    }

    [Fact]
    public void ParseActivation_KeepsInternalCaptchaImageFromFirewallApi()
    {
        var activation = AvitoGeeTestSolveSupport.ParseActivation(
            """{"kind":"unknown","serverKind":"internalCaptcha","image":"data:image/png;base64,aGVsbG8=","clicked":true} """);

        Assert.Equal(AvitoCaptchaKind.Internal, activation.Kind);
        Assert.Equal("data:image/png;base64,aGVsbG8=", activation.ImageData);
    }

    [Fact]
    public void BuildActivateAndProbeScript_ChecksVisibilityBeforeSelectingGeeTest()
    {
        var script = AvitoGeeTestSolveSupport.BuildActivateAndProbeScript();

        Assert.Contains("getComputedStyle", script, StringComparison.Ordinal);
        Assert.Contains("#h-captcha", script, StringComparison.Ordinal);
        Assert.Contains("#geetest_captcha", script, StringComparison.Ordinal);
        Assert.Contains(".geetest_box", script, StringComparison.Ordinal);
        Assert.Contains(".geetest_nine", script, StringComparison.Ordinal);
        Assert.Contains("userAgent", script, StringComparison.Ordinal);
        Assert.Contains("/web/5/firewallCaptcha/get", script, StringComparison.Ordinal);
        Assert.Contains("getInternalImage", script, StringComparison.Ordinal);
    }

    [Fact]
    public void IsLoginGeeTestOverlay_LoginNineGrid_True()
    {
        const string html = """
            <form data-marker="login-form"></form>
            <div class="geetest_box" style="display: block;"><div class="geetest_nine"></div></div>
            """;

        Assert.True(AvitoGeeTestSolveSupport.IsLoginGeeTestOverlay(html));
    }

    [Fact]
    public void IsLoginClickCaptchaOverlay_VisibleClickWidget_True()
    {
        const string html = """
            <form data-marker="login-form"></form>
            <div class="geetest_captcha geetest_boxShow" style="display: block;">
              <div class="geetest_box" style="display: block;">
                <div class="geetest_bg geetest_click"></div>
                <div class="geetest_ques_tips"><img src="hint.png"></div>
                <div class="geetest_submit">Подтвердить</div>
              </div>
            </div>
            """;

        Assert.True(AvitoGeeTestSolveSupport.IsLoginClickCaptchaOverlay(html));
    }

    [Fact]
    public void IsLoginClickCaptchaOverlay_NineGridImageSelection_True()
    {
        const string html = """
            <form data-marker="login-form"></form>
            <div class="geetest_box" style="display: block;">
              <div class="geetest_text_tips">Выберите 3 изображения с</div>
              <div class="geetest_ques_tips"><img src="hint.png"></div>
              <div class="geetest_nine">
                <div class="geetest_item"><div class="geetest_item_img"></div></div>
                <div class="geetest_item"><div class="geetest_item_img"></div></div>
                <div class="geetest_item"><div class="geetest_item_img"></div></div>
              </div>
            </div>
            """;

        Assert.True(AvitoGeeTestSolveSupport.IsLoginClickCaptchaOverlay(html));
    }

    [Fact]
    public void IsLoginGeeTestOverlay_FirewallWidget_False()
    {
        const string html = """
            <div class="firewall-container">
              <div id="geetest_captcha"></div>
              <div class="geetest_box"></div>
            </div>
            """;

        Assert.False(AvitoGeeTestSolveSupport.IsLoginGeeTestOverlay(html));
    }

    [Fact]
    public void IsLoginGeeTestOverlay_PasswordFormWithoutWidget_False()
    {
        const string html = """
            <form data-marker="login-form">
              <input data-marker="login-form/password/input" type="password">
            </form>
            """;

        Assert.False(AvitoGeeTestSolveSupport.IsLoginGeeTestOverlay(html));
    }

    [Fact]
    public void BuildApplyLoginGeeTestScript_InjectsTokensWithoutFirewallVerify()
    {
        var solution = new GeeTestV4Solution(
            CaptchaId: "3d0936b11a2c4a65bbb53635e656c780",
            LotNumber: "lot-login",
            PassToken: "pass-login",
            GenTime: "1693924478",
            CaptchaOutput: "out-login");

        var script = AvitoGeeTestSolveSupport.BuildApplyLoginGeeTestScript(solution);

        Assert.Contains("3d0936b11a2c4a65bbb53635e656c780", script, StringComparison.Ordinal);
        Assert.Contains("lot-login", script, StringComparison.Ordinal);
        Assert.Contains("pass-login", script, StringComparison.Ordinal);
        Assert.Contains("getValidate", script, StringComparison.Ordinal);
        Assert.Contains("geetest_box", script, StringComparison.Ordinal);
        Assert.DoesNotContain("firewallCaptcha/verify", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/web/3/firewallCaptcha/verify", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseLoginApplyResult_AppliedOverlayGone()
    {
        var result = AvitoGeeTestSolveSupport.ParseLoginApplyResult(
            """{"applied":true,"overlayGone":true,"method":"getValidate"}""");

        Assert.True(result.Applied);
        Assert.True(result.OverlayGone);
        Assert.Equal("getValidate", result.Method);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ExtractGeeTestCaptchaId_FromDataAttribute()
    {
        const string html = """<div class="geetest_widget" data-geetest="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"></div>""";

        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", AvitoCaptchaDetector.ExtractGeeTestCaptchaId(html));
    }

    [Fact]
    public void ExtractGeeTestCaptchaId_FallbackToAvitoConstant()
    {
        Assert.Equal(
            AvitoCaptchaDetector.AvitoGeeTestCaptchaId,
            AvitoCaptchaDetector.ExtractGeeTestCaptchaId("<div id=\"geetest_captcha\"></div>"));
    }

    [Fact]
    public void ExtractHCaptchaSiteKey_FromStaticFirewallMarkup()
    {
        const string html = """<div class="h-captcha" data-sitekey="070db171-ddb9-4c93-b7f6-d25d3c9d7e28"></div>""";

        Assert.Equal("070db171-ddb9-4c93-b7f6-d25d3c9d7e28", AvitoGeeTestSolveSupport.ExtractHCaptchaSiteKey(html));
    }
}

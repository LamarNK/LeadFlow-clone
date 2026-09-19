using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoPageStateProbeTests
{
    [Fact]
    public void BuildProbeScript_SeparatesIpBlockFromCaptcha()
    {
        var script = AvitoPageStateScripts.BuildProbeScript();

        Assert.Contains("const hasIpBlock", script, StringComparison.Ordinal);
        Assert.Contains("hasCaptchaChallenge", script, StringComparison.Ordinal);
        Assert.Contains("const hasFirewallIp = obstacle.kind === \"ipBlocked\"", script, StringComparison.Ordinal);
        Assert.Contains("obstacle.profileSwitchOpen", script, StringComparison.Ordinal);
        Assert.Contains("obstacle.profileSwitchCardCount", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "const profileSwitchModalOpen = !!document.querySelector(\"[data-marker='component-profile-switch/root']\")",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProbeScript_DetectsLoginGeeTestOverlay()
    {
        var script = AvitoPageStateScripts.BuildProbeScript();

        Assert.Contains("geetest_box", script, StringComparison.Ordinal);
        Assert.Contains("geetest_popup_wrap", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProbeScript_LoginUiWinsOverLeftoverCaptchaText()
    {
        var script = AvitoPageStateScripts.BuildProbeScript();

        Assert.Contains("const hasCaptcha = hasFirewallIp || obstacle.kind === \"captcha\"", script, StringComparison.Ordinal);
        Assert.Contains("pageKind = \"login\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProbeScript_LoginGeeTestOverlayFallsBackToActiveDomMarker()
    {
        var script = AvitoPageStateScripts.BuildProbeScript();

        Assert.Contains("obstacle.kind", script, StringComparison.Ordinal);
        Assert.Contains("geetest_boxShow", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.getElementById(\"geetest_captcha\") ||", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProbeScript_TreatsVisibilityErrorsAsHidden()
    {
        var script = AvitoPageStateScripts.BuildProbeScript();
        var visibilityStart = script.IndexOf("const isVisibleEl = (el) => {", StringComparison.Ordinal);
        var captchaStart = script.IndexOf("const hasOldWidget", StringComparison.Ordinal);

        Assert.InRange(script.IndexOf("try {", visibilityStart, StringComparison.Ordinal), visibilityStart + 1, captchaStart - 1);
        Assert.InRange(script.IndexOf("catch {", visibilityStart, StringComparison.Ordinal), visibilityStart + 1, captchaStart - 1);
    }

    [Fact]
    public void TryParse_ProfileSwitchModalOpen_DetectsModal()
    {
        const string json = """
            {
              "pageKind":"profileSwitchModal",
              "url":"https://www.avito.ru/profile/dashboard#profile/switch",
              "title":"Avito Pro",
              "profileSwitchModalOpen":true,
              "profileCardsCount":7,
              "currentSubProfileId":"123",
              "currentSubProfileName":"контракт РФ 7",
              "candidatesItemCount":0,
              "hasLoginForm":false,
              "hasCaptcha":false
            }
            """;

        var state = AvitoPageStateProbe.TryParse(json);

        Assert.NotNull(state);
        Assert.Equal(AvitoPageKind.ProfileSwitchModal, state!.PageKind);
        Assert.True(state.ProfileSwitchModalOpen);
        Assert.Equal(7, state.ProfileCardsCount);
        Assert.Equal("контракт РФ 7", state.CurrentSubProfileName);
        Assert.False(state.IsOnCandidates);
    }

    [Fact]
    public void TryParse_JobResponsesCrmPage_IsOnCandidates()
    {
        const string json = """
            {
              "pageKind":"candidates",
              "url":"https://www.avito.ru/profile/job/responses",
              "profileSwitchModalOpen":false,
              "profileCardsCount":0,
              "candidatesItemCount":8,
              "hasLoginForm":false,
              "hasCaptcha":false
            }
            """;

        var state = AvitoPageStateProbe.TryParse(json);

        Assert.NotNull(state);
        Assert.True(state!.IsOnCandidates);
        Assert.Equal(8, state.CandidatesItemCount);
    }

    [Fact]
    public void TryParse_CandidatesPage_IsOnCandidates()
    {
        const string json = """
            {
              "pageKind":"candidates",
              "url":"https://www.avito.ru/profile/candidates",
              "profileSwitchModalOpen":false,
              "profileCardsCount":0,
              "candidatesItemCount":3,
              "hasLoginForm":false,
              "hasCaptcha":false
            }
            """;

        var state = AvitoPageStateProbe.TryParse(json);

        Assert.NotNull(state);
        Assert.True(state!.IsOnCandidates);
        Assert.Equal(3, state.CandidatesItemCount);
    }

    [Fact]
    public void TryParse_LoginForm_DetectsAuthRequired()
    {
        const string json = """
            {
              "pageKind":"login",
              "url":"https://www.avito.ru/profile/login",
              "title":"Вход",
              "profileSwitchModalOpen":false,
              "profileCardsCount":0,
              "candidatesItemCount":0,
              "hasLoginForm":true,
              "hasCaptcha":false,
              "hasFirewallIp":false
            }
            """;

        var state = AvitoPageStateProbe.TryParse(json);

        Assert.NotNull(state);
        Assert.Equal(AvitoPageKind.Login, state!.PageKind);
        Assert.True(state.HasLoginForm);
        Assert.Equal("форма входа", state.DescribeKindRu());
    }

    [Fact]
    public void DescribeKindRu_Login_IsNotUnknownPage()
    {
        var state = new AvitoPageState(
            AvitoPageKind.Login,
            "https://www.avito.ru/profile/login",
            "Вход",
            false,
            0,
            null,
            null,
            0,
            true,
            false);

        Assert.Equal("форма входа", state.DescribeKindRu());
        Assert.DoesNotContain("неизвестная", state.DescribeKindRu(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_FirewallIp_DetectsCaptcha()
    {
        const string json = """
            {
              "pageKind":"captcha",
              "url":"https://www.avito.ru/",
              "title":"Доступ ограничен",
              "profileSwitchModalOpen":false,
              "profileCardsCount":0,
              "candidatesItemCount":0,
              "hasLoginForm":false,
              "hasCaptcha":true,
              "hasFirewallIp":true
            }
            """;

        var state = AvitoPageStateProbe.TryParse(json);

        Assert.NotNull(state);
        Assert.Equal(AvitoPageKind.Captcha, state!.PageKind);
        Assert.True(state.HasFirewallIp);
        Assert.Contains("блок IP", state.DescribeKindRu(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_InsufficientAdvance_DetectsHiddenAdsBanner()
    {
        const string json = """
            {
              "pageKind":"profileItems",
              "url":"https://www.avito.ru/profile/pro/items",
              "title":"Мои объявления",
              "profileSwitchModalOpen":false,
              "profileCardsCount":0,
              "candidatesItemCount":0,
              "hasLoginForm":false,
              "hasCaptcha":false,
              "hasInsufficientAdvance":true
            }
            """;

        var state = AvitoPageStateProbe.TryParse(json);

        Assert.NotNull(state);
        Assert.True(state!.HasInsufficientAdvance);
        Assert.Contains("недостаточно денег", state.DescribeKindRu(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_EmailConfirmationRequired_DetectsConfirmationBanner()
    {
        const string json = """
            {
              "pageKind":"profileItems",
              "url":"https://www.avito.ru/profile/pro/items",
              "title":"Мои объявления",
              "profileSwitchModalOpen":false,
              "profileCardsCount":0,
              "candidatesItemCount":0,
              "hasLoginForm":false,
              "hasCaptcha":false,
              "hasEmailConfirmationRequired":true
            }
            """;

        var state = AvitoPageStateProbe.TryParse(json);

        Assert.NotNull(state);
        Assert.True(state!.HasEmailConfirmationRequired);
        Assert.Contains("почт", state.DescribeKindRu(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_PasswordWasReset_RequiresSmsAndKeepsOnlyMaskedPhone()
    {
        const string json = """
            {
              "pageKind":"login",
              "url":"https://www.avito.ru/profile/pro/items",
              "title":"Avito",
              "profileSwitchModalOpen":false,
              "profileCardsCount":0,
              "candidatesItemCount":0,
              "hasLoginForm":true,
              "hasCaptcha":false,
              "requiresPasswordResetSms":true,
              "passwordResetSmsPhone":"+7 *** ***-**-35"
            }
            """;

        var state = AvitoPageStateProbe.TryParse(json);

        Assert.NotNull(state);
        Assert.True(state!.RequiresPasswordResetSms);
        Assert.Equal("+7 *** ***-**-35", state.PasswordResetSmsPhone);
        var message = AvitoAutomationFailureFormatter.Format("авторизация", state);
        Assert.Contains("SMS", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+7 *** ***-**-35", message, StringComparison.Ordinal);
        Assert.DoesNotContain("546", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProbeScript_DetectsPasswordResetSmsFormAndMasksPhone()
    {
        var script = AvitoPageStateScripts.BuildProbeScript();

        Assert.Contains("password-was-reset", script, StringComparison.Ordinal);
        Assert.Contains("Сработала\\s+защита\\s+профиля", script, StringComparison.Ordinal);
        Assert.Contains("Получить\\s+код\\s+по\\s+смс", script, StringComparison.Ordinal);
        Assert.Contains("passwordResetSmsPhone", script, StringComparison.Ordinal);
        var resetForm = script.IndexOf("const passwordResetForm", StringComparison.Ordinal);
        var loginDom = script.IndexOf("const hasLoginDom", StringComparison.Ordinal);
        Assert.True(resetForm >= 0 && resetForm < loginDom, "SMS-reset form must participate in login detection.");
    }

    [Fact]
    public void BuildProbeScript_DetectsTransientErrorPageBeforeProfileItemsUrl()
    {
        var script = AvitoPageStateScripts.BuildProbeScript();

        Assert.Contains("const hasTransientError", script, StringComparison.Ordinal);
        Assert.Contains(@"Попробуйте\s+обновить\s+страницу\s+или\s+загляните\s+позже", script, StringComparison.Ordinal);
        Assert.Contains(@"обязательно\s+всё\s+починим", script, StringComparison.Ordinal);
        Assert.Contains("pageKind = \"transientError\"", script, StringComparison.Ordinal);

        var transientKindIndex = script.IndexOf("pageKind = \"transientError\"", StringComparison.Ordinal);
        var profileItemsKindIndex = script.IndexOf("pageKind = \"profileItems\"", StringComparison.Ordinal);
        Assert.True(transientKindIndex > 0 && profileItemsKindIndex > transientKindIndex);
    }

    [Fact]
    public void TryParse_TransientErrorOnProfileItemsUrl_IsTransientPageError()
    {
        const string json = """
            {
              "pageKind":"transientError",
              "url":"https://www.avito.ru/profile/pro/items",
              "title":"Мои объявления",
              "profileSwitchModalOpen":false,
              "profileCardsCount":0,
              "candidatesItemCount":0,
              "hasLoginForm":false,
              "hasCaptcha":false,
              "hasTransientError":true
            }
            """;

        var state = AvitoPageStateProbe.TryParse(json);

        Assert.NotNull(state);
        Assert.Equal(AvitoPageKind.TransientError, state!.PageKind);
        Assert.True(state.HasTransientError);
        Assert.True(state.IsTransientPageError);
        Assert.Contains("прокси", state.DescribeKindRu(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Мои объявления", state.DescribeKindRu(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildClickRefreshOnTransientErrorScript_RequiresErrorTextAndExactButton()
    {
        var script = AvitoPageStateScripts.BuildClickRefreshOnTransientErrorScript();

        Assert.Contains(@"Попробуйте\s+обновить\s+страницу", script, StringComparison.Ordinal);
        Assert.Contains("Обновить", script, StringComparison.Ordinal);
        Assert.Contains("btn.click()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeForDiagnostics_IncludesModalAndSubProfile()
    {
        var state = new AvitoPageState(
            AvitoPageKind.ProfileSwitchModal,
            "https://www.avito.ru/profile/dashboard",
            "Avito",
            true,
            5,
            "42",
            "контракт РФ 7",
            0,
            false,
            false);

        var text = state.DescribeForDiagnostics();

        Assert.Contains("модалка", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("контракт РФ 7", text, StringComparison.Ordinal);
    }
}

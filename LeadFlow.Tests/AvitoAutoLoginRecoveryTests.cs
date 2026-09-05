using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAutoLoginRecoveryTests
{
    [Fact]
    public void TryParseProbe_GuestHeader_DetectsNeedsLogin()
    {
        const string json = """
            {
              "needsLogin": true,
              "isAuthorized": false,
              "hasCaptcha": false,
              "hasLoginForm": false,
              "hasUsersList": false,
              "hasSavedUserCard": false,
              "hasOtherProfileLink": false,
              "hasProfileChooser": false,
              "hasCredentialInputs": false,
              "hasGuestLoginButton": true,
              "hasLoggedInProfile": false,
              "hasPasswordValue": false,
              "hasSubmitButton": false,
              "url": "https://www.avito.ru/elniki"
            }
            """;

        var state = AvitoAutoLoginRecovery.TryParseProbe(json);

        Assert.NotNull(state);
        Assert.True(state!.NeedsLogin);
        Assert.True(state.HasGuestLoginButton);
        Assert.False(state.IsAuthorized);
    }

    [Fact]
    public void TryParseProbe_SavedPasswordForm_DetectsSubmitReady()
    {
        const string json = """
            {
              "needsLogin": true,
              "isAuthorized": false,
              "hasCaptcha": false,
              "hasLoginForm": true,
              "hasUsersList": false,
              "hasSavedUserCard": false,
              "hasOtherProfileLink": false,
              "hasProfileChooser": false,
              "hasCredentialInputs": true,
              "hasGuestLoginButton": false,
              "hasLoggedInProfile": false,
              "hasPasswordValue": true,
              "hasSubmitButton": true,
              "url": "https://www.avito.ru/#login"
            }
            """;

        var state = AvitoAutoLoginRecovery.TryParseProbe(json);

        Assert.NotNull(state);
        Assert.True(state!.HasPasswordValue);
        Assert.True(state.HasSubmitButton);
        Assert.True(state.HasCredentialInputs);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    public void IsSessionRecovered_MatchesExpected(bool isAuthorized, bool needsLogin, bool hasCaptcha, bool expected)
    {
        var state = new AvitoAutoLoginRecovery.ProbeState(
            NeedsLogin: needsLogin,
            IsAuthorized: isAuthorized,
            HasCaptcha: hasCaptcha,
            HasLoginForm: false,
            HasUsersList: false,
            HasSavedUserCard: false,
            HasOtherProfileLink: false,
            HasProfileChooser: false,
            HasCredentialInputs: false,
            HasGuestLoginButton: false,
            HasLoggedInProfile: false,
            HasPasswordValue: false,
            HasSubmitButton: false,
            Url: "https://www.avito.ru/profile/dashboard");

        Assert.Equal(expected, AvitoAutoLoginRecovery.IsSessionRecovered(state));
    }

    [Fact]
    public void IsSessionRecovered_NullState_ReturnsFalse() =>
        Assert.False(AvitoAutoLoginRecovery.IsSessionRecovered(null));

    [Fact]
    public void TryCreate_RequiresLoginAndPassword()
    {
        Assert.Null(AvitoLoginCredentials.TryCreate(null, "x"));
        Assert.Null(AvitoLoginCredentials.TryCreate("user", null));
        Assert.Null(AvitoLoginCredentials.TryCreate("  ", "x"));
        Assert.Null(AvitoLoginCredentials.TryCreate("user", ""));

        var creds = AvitoLoginCredentials.TryCreate("  +7999  ", "secret");
        Assert.NotNull(creds);
        Assert.Equal("+7999", creds!.Login);
        Assert.Equal("secret", creds.Password);
        Assert.True(creds.IsUsable);
    }

    [Fact]
    public void BuildFillCredentialsAndSubmitScript_EscapesValues()
    {
        var script = AvitoAutoLoginScripts.BuildFillCredentialsAndSubmitScript(
            "user\"'<>",
            "p@ss\nword");

        Assert.Contains("const login = ", script);
        Assert.Contains("const password = ", script);
        Assert.DoesNotContain("user\"'<>", script);
        Assert.Contains("\\n", script);
    }

    [Fact]
    public void TryParseProbe_AuthorizedProfile_ReturnsRecoveredState()
    {
        const string json = """
            {
              "needsLogin": false,
              "isAuthorized": true,
              "hasCaptcha": false,
              "hasLoginForm": false,
              "hasUsersList": false,
              "hasSavedUserCard": false,
              "hasOtherProfileLink": false,
              "hasProfileChooser": false,
              "hasCredentialInputs": false,
              "hasGuestLoginButton": false,
              "hasLoggedInProfile": true,
              "hasPasswordValue": false,
              "hasSubmitButton": false,
              "url": "https://www.avito.ru/profile/job/responses"
            }
            """;

        var state = AvitoAutoLoginRecovery.TryParseProbe(json);

        Assert.NotNull(state);
        Assert.True(state!.IsAuthorized);
        Assert.False(state.NeedsLogin);
    }

    [Fact]
    public void TryParseProbe_SavedProfileChooser_DetectsCardAndOtherProfileLink()
    {
        const string json = """
            {
              "needsLogin": true,
              "isAuthorized": false,
              "hasCaptcha": false,
              "hasLoginForm": true,
              "hasUsersList": true,
              "hasSavedUserCard": true,
              "hasOtherProfileLink": true,
              "hasProfileChooser": true,
              "hasCredentialInputs": false,
              "hasGuestLoginButton": false,
              "hasLoggedInProfile": false,
              "hasPasswordValue": false,
              "hasSubmitButton": false,
              "url": "https://www.avito.ru/#login"
            }
            """;

        var state = AvitoAutoLoginRecovery.TryParseProbe(json);

        Assert.NotNull(state);
        Assert.True(state!.NeedsLogin);
        Assert.True(state.HasSavedUserCard);
        Assert.True(state.HasOtherProfileLink);
        Assert.True(state.HasProfileChooser);
        Assert.False(state.HasCredentialInputs);
    }

    [Fact]
    public void BuildSelectSavedUserScript_TargetsSavedCardNotOtherProfile()
    {
        var script = AvitoAutoLoginScripts.BuildSelectSavedUserScript();

        Assert.Contains("user/link", script);
        Assert.Contains("users-list", script);
        Assert.Contains("user/delete", script);
        Assert.Contains("войти\\s+в\\s+другой\\s+профиль", script);
        Assert.Contains("phone_card", script);
        Assert.Contains("no_matching_profile", script);
        Assert.DoesNotContain("login-form-with-avatar", script);
        Assert.DoesNotContain("another-profile-link", script);
    }

    [Fact]
    public void BuildSelectSavedUserScript_PrefersOrbitPhoneWhenProvided()
    {
        var script = AvitoAutoLoginScripts.BuildSelectSavedUserScript("+7 901 078-51-82");

        Assert.Contains("901 078-51-82", script);
        Assert.Contains("saved_user_matched", script);
        Assert.Contains("no_matching_profile", script);
        Assert.Contains("normalizePhone", script);
    }

    [Fact]
    public void BuildSelectSavedUserScript_DispatchesCompleteMouseGesture()
    {
        var script = AvitoAutoLoginScripts.BuildSelectSavedUserScript();

        Assert.Contains("pointerdown", script);
        Assert.Contains("mousedown", script);
        Assert.Contains("mouseup", script);
    }

    [Fact]
    public void BuildFillCredentialsAndSubmitScript_RequiresOrbitValuesBeforeSubmit()
    {
        var script = AvitoAutoLoginScripts.BuildFillCredentialsAndSubmitScript("+79001234567", "test-password");

        Assert.Contains("orbit_password_not_applied", script);
        Assert.Contains("orbit_login_not_applied", script);
        Assert.Contains("requestSubmit", script);
        Assert.Contains("input[name='login'][autocomplete='username']", script);
    }

    [Fact]
    public void BuildSwitchToOtherProfileScript_ContainsOtherProfileText()
    {
        var script = AvitoAutoLoginScripts.BuildSwitchToOtherProfileScript();

        Assert.Contains("войти\\s+в\\s+другой\\s+профиль", script);
        Assert.Contains("login-form/other", script);
        Assert.Contains("users-list/button", script);
        Assert.DoesNotContain("another-profile-link", script);
        Assert.DoesNotContain("вернуться\\s+к\\s+списку", script);
    }

    [Fact]
    public void BuildProbeScript_DetectsUsersListAsNeedsLogin()
    {
        var script = AvitoAutoLoginScripts.BuildProbeScript();

        Assert.Contains("hasUsersList", script);
        Assert.Contains("users-list", script);
        Assert.Contains("user/link", script);
        Assert.Contains("hasProfileChooser", script);
        Assert.Contains("login-form-with-avatar", script);
        Assert.DoesNotContain("[data-marker='login-form-with-avatar'] button", script);
        Assert.DoesNotContain("[data-marker*='other-profile']", script);
        Assert.DoesNotContain("another-profile-link", script);
    }

    [Fact]
    public void BuildProbeScript_DetectsLoginGeeTestOverlay()
    {
        var script = AvitoAutoLoginScripts.BuildProbeScript();

        Assert.Contains("geetest_box", script, StringComparison.Ordinal);
        Assert.Contains("geetest_nine", script, StringComparison.Ordinal);
        Assert.Contains("hasCaptchaWidget", script, StringComparison.Ordinal);
        Assert.Contains("hasLoginUi", script, StringComparison.Ordinal);
        Assert.Contains("liveCaptchaWidget", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProbeScript_TreatsVisibilityErrorsAsHidden()
    {
        var script = AvitoAutoLoginScripts.BuildProbeScript().Replace("\r\n", "\n", StringComparison.Ordinal);

        var visibilityStart = script.IndexOf("const isVisibleEl = (el) => {", StringComparison.Ordinal);
        var captchaStart = script.IndexOf("const liveCaptchaWidget", StringComparison.Ordinal);

        Assert.InRange(script.IndexOf("try {", visibilityStart, StringComparison.Ordinal), visibilityStart + 1, captchaStart - 1);
        Assert.InRange(script.IndexOf("catch {", visibilityStart, StringComparison.Ordinal), visibilityStart + 1, captchaStart - 1);
    }

    [Fact]
    public void BuildProbeScript_LoginGeeTestOverlayFallsBackToActiveDomMarker()
    {
        var script = AvitoAutoLoginScripts.BuildProbeScript();

        Assert.Contains("hasGeeTestOverlayDom", script, StringComparison.Ordinal);
        Assert.Contains("geetest_boxShow", script, StringComparison.Ordinal);
        Assert.Contains("liveCaptchaWidget || hasGeeTestOverlayDom || hasFirewallDom", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeRetryPolicy_UsesFourAttemptsAndPostCaptchaDelay()
    {
        Assert.Equal(4, AvitoAutoLoginRecovery.MaxProbeAttempts);
        Assert.InRange(AvitoAutoLoginRecovery.ProbeRetryDelayMs, 400, 800);
    }

    [Fact]
    public void BuildJsonEvaluationExpression_RemovesTerminalStatementDelimiter()
    {
        var expression = AvitoAutoLoginRecovery.BuildJsonEvaluationExpression(
            AvitoAutoLoginScripts.BuildProbeScript());

        Assert.StartsWith("JSON.stringify((() =>", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("();)", expression, StringComparison.Ordinal);
        Assert.EndsWith("})())", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void HasVisibleLoginUi_SavedUserListPreventsSessionRefresh()
    {
        var state = new AvitoAutoLoginRecovery.ProbeState(
            NeedsLogin: true,
            IsAuthorized: false,
            HasCaptcha: false,
            HasLoginForm: false,
            HasUsersList: true,
            HasSavedUserCard: true,
            HasOtherProfileLink: true,
            HasProfileChooser: true,
            HasCredentialInputs: false,
            HasGuestLoginButton: false,
            HasLoggedInProfile: false,
            HasPasswordValue: false,
            HasSubmitButton: false,
            Url: "https://www.avito.ru/profile/pro/items");

        Assert.True(AvitoAutoLoginRecovery.HasVisibleLoginUi(state));
    }

    [Fact]
    public void Recovery_ProbesForSavedUserBeforeRefreshingAnInvisibleLoginUi()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LeadFlow.Core", "Services", "Avito", "AvitoAutoLoginRecovery.cs"));

        var refresh = source.IndexOf("TryRefreshSessionAsync(page, steps, cancellationToken)", StringComparison.Ordinal);
        var savedUserProbe = source.IndexOf("HasSavedUserCardInDomAsync", StringComparison.Ordinal);

        Assert.True(savedUserProbe >= 0, "Recovery must query users-list/user/link before a refresh.");
        Assert.True(savedUserProbe < refresh, "Recovery must preserve a visible saved-profile list instead of reloading it.");
    }

    [Fact]
    public void DecideCaptcha_IgnoresWhenNoCaptcha()
    {
        var state = new AvitoAutoLoginRecovery.ProbeState(
            NeedsLogin: true,
            IsAuthorized: false,
            HasCaptcha: false,
            HasLoginForm: true,
            HasUsersList: false,
            HasSavedUserCard: false,
            HasOtherProfileLink: false,
            HasProfileChooser: false,
            HasCredentialInputs: true,
            HasGuestLoginButton: false,
            HasLoggedInProfile: false,
            HasPasswordValue: true,
            HasSubmitButton: true,
            Url: "https://www.avito.ru/#login");

        Assert.Equal(
            AvitoAutoLoginRecovery.LoginCaptchaDecision.Ignore,
            AvitoAutoLoginRecovery.DecideCaptcha(state, solverAvailable: true, attempts: 0, maxAttempts: 3));
    }

    [Fact]
    public void DecideCaptcha_SolvesWhenOverlayAndSolverAvailable()
    {
        var state = new AvitoAutoLoginRecovery.ProbeState(
            NeedsLogin: true,
            IsAuthorized: false,
            HasCaptcha: true,
            HasLoginForm: true,
            HasUsersList: false,
            HasSavedUserCard: false,
            HasOtherProfileLink: false,
            HasProfileChooser: false,
            HasCredentialInputs: true,
            HasGuestLoginButton: false,
            HasLoggedInProfile: false,
            HasPasswordValue: true,
            HasSubmitButton: true,
            Url: "https://www.avito.ru/#login");

        Assert.Equal(
            AvitoAutoLoginRecovery.LoginCaptchaDecision.Solve,
            AvitoAutoLoginRecovery.DecideCaptcha(state, solverAvailable: true, attempts: 0, maxAttempts: 3));
        Assert.Equal(
            AvitoAutoLoginRecovery.LoginCaptchaDecision.Abort,
            AvitoAutoLoginRecovery.DecideCaptcha(state, solverAvailable: false, attempts: 0, maxAttempts: 3));
        Assert.Equal(
            AvitoAutoLoginRecovery.LoginCaptchaDecision.Abort,
            AvitoAutoLoginRecovery.DecideCaptcha(state, solverAvailable: true, attempts: 3, maxAttempts: 3));
    }

    [Fact]
    public void BuildFillCredentialsAndSubmitScript_TreatsHiddenReadonlyLoginAsPasswordOnly()
    {
        var script = AvitoAutoLoginScripts.BuildFillCredentialsAndSubmitScript("+79010785182", "orbit-secret");

        Assert.Contains("passwordOnlyForm", script);
        Assert.Contains("loginInput.readOnly", script);
        Assert.Contains("loginInput.style.display === \"none\"", script);
        Assert.Contains("[data-marker='login-form/password/input']", script);
        Assert.Contains("[data-marker='login-form/submit']", script);
    }
}

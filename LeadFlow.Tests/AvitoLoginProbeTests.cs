using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoLoginProbeTests
{
    [Fact]
    public void TryParse_AuthAppRoot_DetectsLogin()
    {
        const string json = """
            {
              "hasLogin": true,
              "url": "https://www.avito.ru/profile/login",
              "title": "Вход",
              "urlSuggestsLogin": true,
              "titleSuggestsLogin": true,
              "hasLoginDom": true,
              "hasLoginHtml": true,
              "hasLoginText": false
            }
            """;

        var detection = AvitoLoginProbe.TryParse(json);

        Assert.NotNull(detection);
        Assert.True(detection!.HasLogin);
        Assert.Equal("Вход", detection.Title);
    }

    [Fact]
    public void TryParse_NoLogin_ReturnsNull()
    {
        const string json = """
            {
              "hasLogin": false,
              "url": "https://www.avito.ru/profile/candidates",
              "title": "Отклики"
            }
            """;

        Assert.Null(AvitoLoginProbe.TryParse(json));
    }

    [Fact]
    public void SuggestsLogin_UnknownPageWithLoginTitle_ReturnsTrue()
    {
        var state = new AvitoPageState(
            AvitoPageKind.Unknown,
            "https://www.avito.ru/profile/candidates",
            "Вход",
            false,
            0,
            null,
            null,
            0,
            false,
            false);

        Assert.True(AvitoAutomationFailureFormatter.SuggestsLogin(state));
        Assert.Contains(
            "повторная авторизация",
            AvitoAutomationFailureFormatter.Format("сбор откликов", state, null),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuggestsLogin_ProItemsPageWithoutLoginForm_IsFalse()
    {
        // Скрин: кабинет /profile/pro/items + баннер «Зарегистрироваться» — hasLoginForm должен быть false от JS.
        var state = new AvitoPageState(
            AvitoPageKind.ProfileItems,
            "https://www.avito.ru/profile/pro/items",
            "Объявления — Avito Pro",
            false,
            0,
            null,
            "Кадровый отдел города Владимир",
            0,
            false,
            false);

        Assert.False(AvitoAutomationFailureFormatter.SuggestsLogin(state));
        Assert.NotEqual(AvitoSubProfileIssueKind.AuthRequired, AvitoAutomationFailureFormatter.MapDiagnosticKind(state, null));
    }

    [Fact]
    public void PageStateScript_GatesSoftLoginSignalsWhenLoggedIn()
    {
        var script = AvitoPageStateScripts.BuildProbeScript();

        // Промо-кнопки баннеров не должны входить в regex формы входа.
        Assert.DoesNotContain("|зарегистрироваться|", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("|зарегистрир", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("osp-sidebar/tools/profile/name", script, StringComparison.Ordinal);
        Assert.Contains("osp-sidebar/tools/money", script, StringComparison.Ordinal);
        Assert.Contains("softLoginSignals", script, StringComparison.Ordinal);
        Assert.Contains("!hasLoggedInProfile", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectionScripts_RecognizeSavedUsersListScreen()
    {
        var pageState = AvitoPageStateScripts.BuildProbeScript();
        var autoLoginProbe = AvitoAutoLoginScripts.BuildProbeScript();
        var selectUser = AvitoAutoLoginScripts.BuildSelectSavedUserScript();
        var switchOther = AvitoAutoLoginScripts.BuildSwitchToOtherProfileScript();

        // Экран «Вход» со списком профилей (users-list / user/link) должен детектиться как login.
        Assert.Contains("hasSavedUsersList", pageState, StringComparison.Ordinal);
        Assert.Contains("users-list", pageState, StringComparison.Ordinal);
        Assert.Contains("user/link", pageState, StringComparison.Ordinal);
        Assert.Contains("login-form-with-avatar", pageState, StringComparison.Ordinal);

        Assert.Contains("users-list", autoLoginProbe, StringComparison.Ordinal);
        Assert.Contains("user/link", autoLoginProbe, StringComparison.Ordinal);
        Assert.Contains("login-form-with-avatar", autoLoginProbe, StringComparison.Ordinal);

        Assert.Contains("user/link", selectUser, StringComparison.Ordinal);
        Assert.Contains("users-list/button", switchOther, StringComparison.Ordinal);
        Assert.Contains("войти\\s+в\\s+другой\\s+профиль", selectUser, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FillCredentialsScript_DoesNotOverwriteLockedLoginOnPasswordOnlyForm()
    {
        var script = AvitoAutoLoginScripts.BuildFillCredentialsAndSubmitScript("+79001234567", "secret");

        Assert.Contains("passwordOnlyForm", script, StringComparison.Ordinal);
        Assert.Contains("loginLocked", script, StringComparison.Ordinal);
        Assert.Contains("!passwordOnlyForm", script, StringComparison.Ordinal);
    }
}
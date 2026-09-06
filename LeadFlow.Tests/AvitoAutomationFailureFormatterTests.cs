using System.Text.Json;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAutomationFailureFormatterTests
{
    [Fact]
    public void Format_WhenModalStillOpen_ReturnsRussianMessage()
    {
        var state = new AvitoPageState(
            AvitoPageKind.ProfileSwitchModal,
            "https://www.avito.ru/profile/dashboard",
            null,
            true,
            7,
            "1",
            "контракт РФ 7",
            0,
            false,
            false);

        var message = AvitoAutomationFailureFormatter.Format(
            "отклики",
            state,
            recoveryAttempts: ["закрыть модалку", "повторный переход"]);

        Assert.Contains("модалка", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7", message, StringComparison.Ordinal);
        Assert.Contains("Попытки", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Boolean", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_JsonException_OnDashboard_ExplainsNavigationMismatchNotRawJson()
    {
        var state = new AvitoPageState(
            AvitoPageKind.Dashboard,
            "https://www.avito.ru/profile/dashboard",
            null,
            false,
            0,
            null,
            null,
            0,
            false,
            false);

        var message = AvitoAutomationFailureFormatter.Format(
            "сбор откликов",
            state,
            new JsonException("The JSON value could not be converted to System.Boolean."));

        Assert.Contains("не удалось перейти к откликам", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("главная панель", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Boolean", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_WhenLoginPage_ReturnsAuthMessage()
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

        var message = AvitoAutomationFailureFormatter.Format("переключение субпрофиля", state);
        var kind = AvitoAutomationFailureFormatter.MapDiagnosticKind(state, null);

        Assert.Equal(AvitoSubProfileIssueKind.AuthRequired, kind);
        Assert.Contains("автовход", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("авторизац", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LooksLoggedIn_RequiresCabinetMarkersNotJustItemsUrl()
    {
        var guestOnItems = new AvitoPageState(
            AvitoPageKind.ProfileItems,
            "https://www.avito.ru/profile/pro/items",
            "Avito",
            false,
            0,
            null,
            null,
            0,
            HasLoginForm: false,
            HasCaptcha: false);

        var cabinet = guestOnItems with { CurrentSubProfileId = "439640166", CurrentSubProfileName = "Кадровый отдел 6" };

        Assert.False(guestOnItems.LooksLoggedIn);
        Assert.True(cabinet.LooksLoggedIn);
        Assert.True(AvitoAutomationFailureFormatter.ShouldAttemptAutoLoginAfterCaptcha(null));
        Assert.True(AvitoAutomationFailureFormatter.ShouldAttemptAutoLoginAfterCaptcha(guestOnItems));
        Assert.False(AvitoAutomationFailureFormatter.ShouldAttemptAutoLoginAfterCaptcha(cabinet));
    }

    [Fact]
    public void Format_WhenFirewallIp_ReturnsIpBlockMessage()
    {
        var state = new AvitoPageState(
            AvitoPageKind.Captcha,
            "https://www.avito.ru/",
            "Доступ ограничен",
            false,
            0,
            null,
            null,
            0,
            false,
            true,
            HasFirewallIp: true);

        var message = AvitoAutomationFailureFormatter.Format("переключение субпрофиля", state);
        var kind = AvitoAutomationFailureFormatter.MapDiagnosticKind(state, null);

        Assert.Equal(AvitoSubProfileIssueKind.IpBlock, kind);
        Assert.True(AvitoAutomationFailureFormatter.IsAccountBlockingIssue(kind));
        Assert.False(AvitoAutomationFailureFormatter.IsAccountBlockingIssue(AvitoSubProfileIssueKind.Captcha));
        Assert.Contains("проблема с IP", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("капча", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_WhenAdvanceIsInsufficient_ExplainsWhyResponsesAreUnavailable()
    {
        var state = new AvitoPageState(
            AvitoPageKind.ProfileItems,
            "https://www.avito.ru/profile/pro/items",
            "Мои объявления",
            false,
            0,
            null,
            null,
            0,
            false,
            false,
            HasInsufficientAdvance: true);

        var message = AvitoAutomationFailureFormatter.Format("сбор откликов", state);
        var kind = AvitoAutomationFailureFormatter.MapDiagnosticKind(state, null);

        Assert.Equal(AvitoSubProfileIssueKind.InsufficientAdvance, kind);
        Assert.False(AvitoAutomationFailureFormatter.IsAccountBlockingIssue(kind));
        Assert.Contains("недостаточно денег", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("отклик", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("пополн", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_WhenEmailConfirmationIsRequired_ExplainsRequiredAction()
    {
        var state = new AvitoPageState(
            AvitoPageKind.ProfileItems,
            "https://www.avito.ru/profile/pro/items",
            "Мои объявления",
            false,
            0,
            null,
            null,
            0,
            false,
            false,
            HasEmailConfirmationRequired: true);

        var message = AvitoAutomationFailureFormatter.Format("проверка объявлений", state);
        var kind = AvitoAutomationFailureFormatter.MapDiagnosticKind(state, null);

        Assert.Equal(AvitoSubProfileIssueKind.EmailConfirmationRequired, kind);
        Assert.Contains("почт", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("письм", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("подтверд", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_WhenPasswordWasReset_RequiresSmsInsteadOfAnotherPasswordRetry()
    {
        var state = new AvitoPageState(
            AvitoPageKind.Login,
            "https://www.avito.ru/profile/pro/items",
            "Avito",
            false,
            0,
            null,
            null,
            0,
            true,
            false,
            RequiresPasswordResetSms: true,
            PasswordResetSmsPhone: "+7 *** ***-**-35");

        var message = AvitoAutomationFailureFormatter.Format("сбор откликов", state);

        Assert.Contains("SMS", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("сбросил пароль", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+7 *** ***-**-35", message, StringComparison.Ordinal);
        Assert.DoesNotContain("546", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_WhenFirewallIpOnLoginUrl_PrefersIpBlockOverLogin()
    {
        var state = new AvitoPageState(
            AvitoPageKind.Captcha,
            "https://www.avito.ru/profile/login",
            "Доступ ограничен: проблема с IP",
            false,
            0,
            null,
            null,
            0,
            false,
            true,
            HasFirewallIp: true);

        var message = AvitoAutomationFailureFormatter.Format("переключение субпрофиля", state);
        var kind = AvitoAutomationFailureFormatter.MapDiagnosticKind(state, null);

        Assert.Equal(AvitoSubProfileIssueKind.IpBlock, kind);
        Assert.Contains("проблема с IP", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("авторизац", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_WhenTransientErrorPage_ExplainsRefreshAndProxy()
    {
        var state = new AvitoPageState(
            AvitoPageKind.TransientError,
            "https://www.avito.ru/profile/pro/items",
            "Мои объявления",
            false,
            0,
            null,
            "Кадровый отдел 3",
            0,
            false,
            false,
            HasTransientError: true);

        var message = AvitoAutomationFailureFormatter.Format("переключение субпрофиля", state);
        var kind = AvitoAutomationFailureFormatter.MapDiagnosticKind(state, null);

        Assert.Equal(AvitoSubProfileIssueKind.SwitchFailed, kind);
        Assert.False(AvitoAutomationFailureFormatter.IsAccountBlockingIssue(kind));
        Assert.Contains("обновите страницу", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("прокси", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ошибка на шаге", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_WhenCandidatesPageDuringSwitch_ReturnsLoadOrOverlayHint()
    {
        var state = new AvitoPageState(
            AvitoPageKind.Candidates,
            "https://www.avito.ru/profile/job/responses",
            "Отклики",
            false,
            0,
            "433959755",
            "Работа вахта",
            0,
            false,
            false);

        var message = AvitoAutomationFailureFormatter.Format("переключение субпрофиля", state);

        Assert.Contains("всплывающее окно", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("звонить", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Format_JsonException_WithoutPageState_UsesGenericContextMessage()
    {
        var message = AvitoAutomationFailureFormatter.Format(
            "чтение DOM",
            null,
            new JsonException("The JSON value could not be converted to System.Boolean."));

        Assert.Contains("сбой чтения состояния", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Boolean", message, StringComparison.OrdinalIgnoreCase);
    }
}

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
        Assert.Contains("авторизац", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("не переключился", message, StringComparison.OrdinalIgnoreCase);
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

        Assert.Equal(AvitoSubProfileIssueKind.Captcha, kind);
        Assert.Contains("проблема с IP", message, StringComparison.OrdinalIgnoreCase);
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
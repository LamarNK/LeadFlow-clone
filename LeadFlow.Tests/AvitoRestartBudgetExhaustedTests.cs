using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Worker;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoRestartBudgetExhaustedTests
{
    [Fact]
    public void Message_ExplainsReason_InsteadOfFakeCdpTimeout()
    {
        var ex = new AvitoRestartBudgetExhaustedException(2, "Captcha", 3);

        Assert.DoesNotContain(
            LeadFlow.Core.Services.AdsPower.AdsPowerCdpGuard.TimeoutPrefix,
            ex.Message,
            StringComparison.Ordinal);
        Assert.Contains("перезагружалась", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Captcha", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptchaLoop_DetectedByLastObstacle()
    {
        Assert.True(new AvitoRestartBudgetExhaustedException(2, "Captcha", 1).LooksLikeCaptchaLoop);
        Assert.True(new AvitoRestartBudgetExhaustedException(2, "IpBlocked", 1).LooksLikeCaptchaLoop);
        Assert.False(new AvitoRestartBudgetExhaustedException(2, "TransientError", 1).LooksLikeCaptchaLoop);
        Assert.False(new AvitoRestartBudgetExhaustedException(2, null, 1).LooksLikeCaptchaLoop);
    }

    [Fact]
    public void Retry_CaptchaLoop_GetsLongCooldown()
    {
        var ex = new AvitoRestartBudgetExhaustedException(2, "Captcha", 3);

        Assert.Equal(WorkerAdsPowerPassRetry.CaptchaLoopCooldownDelay, WorkerAdsPowerPassRetry.FromException(ex));
        Assert.Equal("Warning", WorkerAdsPowerPassRetry.EventType(ex));
    }

    [Fact]
    public void Retry_NonCaptchaLoop_GetsRegularTransientDelay()
    {
        var ex = new AvitoRestartBudgetExhaustedException(2, "TransientError", 3);

        Assert.Equal(WorkerAdsPowerPassRetry.Delay, WorkerAdsPowerPassRetry.FromException(ex));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Escalate_CaptchaCooldownBase_IsNeverTrimmed(int consecutiveFailures)
    {
        var escalated = WorkerAdsPowerPassRetry.Escalate(
            WorkerAdsPowerPassRetry.CaptchaLoopCooldownDelay,
            consecutiveFailures);

        Assert.Equal(WorkerAdsPowerPassRetry.CaptchaLoopCooldownDelay, escalated);
    }
}

public sealed class AvitoAutomationFailureSessionContextTests
{
    [Fact]
    public void Fallback_WithoutSessionContext_IsUnchanged()
    {
        var message = AvitoAutomationFailureFormatter.Format("переключение субпрофиля", null, null);

        Assert.Equal("не удалось переключить субпрофиль: клик по карточке не завершился.", message);
    }

    [Fact]
    public void Fallback_WithSessionContext_AppendsDiagnostics()
    {
        var message = AvitoAutomationFailureFormatter.Format(
            "переключение субпрофиля",
            null,
            null,
            sessionContext: "Recovering, препятствие: Captcha/geetest, поколение 2, восстановлений: 3");

        Assert.StartsWith("не удалось переключить субпрофиль: ", message, StringComparison.Ordinal);
        Assert.Contains("Recovering", message, StringComparison.Ordinal);
        Assert.Contains("поколение 2", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ошибка на шаге", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InnerExceptionMessage_WinsOverSessionContext()
    {
        var message = AvitoAutomationFailureFormatter.Format(
            "сбор откликов",
            null,
            new AvitoNetworkUnavailableException(AvitoNetworkErrorKind.NoInternet, "ERR_INTERNET_DISCONNECTED"),
            sessionContext: "Running, препятствие: нет, поколение 0, восстановлений: 0");

        Assert.Contains("нет доступа в интернет", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Сессия:", message, StringComparison.Ordinal);
    }
}

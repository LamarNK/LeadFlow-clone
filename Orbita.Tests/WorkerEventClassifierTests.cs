using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerEventClassifierTests
{
    [Theory]
    [InlineData("капча / блок IP", "captcha", "blocked")]
    [InlineData("блок IP", "ip_block", "blocked")]
    [InlineData("нужен вход", "auth", "auth")]
    [InlineData("не переключился", "switch", "automation")]
    [InlineData("ошибка парсинга", "error", "parsing")]
    [InlineData("таймаут", "error", "network")]
    [InlineData("лимит частоты AdsPower", "error", "api")]
    [InlineData("дневной лимит AdsPower", "error", "api")]
    [InlineData("профиль занят", "error", "api")]
    [InlineData("проблема", "error", "automation")]
    public void IssueLabel_MapsToExpectedTypes(string label, string eventType, string errorType)
    {
        var message = BuildIssueMessage(label, "детали.");

        Assert.Equal(label, WorkerEventClassifier.TryParseIssueLabel(message));
        Assert.Equal(eventType, WorkerEventClassifier.MapIssueLabelToEventType(message));
        Assert.Equal(errorType, WorkerEventClassifier.MapIssueLabelToErrorType(message));
    }

    [Fact]
    public void IsNewResponseCandidate_RejectsWarningLevel_EvenWithOtlikWord()
    {
        var text = "субпрофиль — проблема: не удалось перейти к откликам: сейчас неизвестная страница.";

        Assert.False(WorkerEventClassifier.IsNewResponseCandidate(text, "warning"));
    }

    [Fact]
    public void IsNewResponseCandidate_AllowsSuccessLevel_ForRealResponse()
    {
        Assert.True(WorkerEventClassifier.IsNewResponseCandidate("новый отклик отправлен в crm", "success"));
    }

    [Fact]
    public void IsCaptcha_DetectsJsonKind_BeforeNetworkHeuristics()
    {
        var details = """{"kind":"hCaptcha","url":"https://www.avito.ru/profile/candidates"}""";

        Assert.True(WorkerEventClassifier.IsCaptcha("проверка", details));
        Assert.False(WorkerEventClassifier.IsNetworkFailure(details));
    }

    [Fact]
    public void IsIpBlock_DoesNotOfferCaptchaHandling()
    {
        const string message = "Субпрофиль «Контракт9» · аккаунт «Avito 14» — блок IP: доступ ограничен: проблема с IP.";
        const string details = """{"kind":"firewall","url":"https://www.avito.ru/profile/candidates"}""";

        Assert.True(WorkerEventClassifier.IsIpBlock(message, details));
        Assert.False(WorkerEventClassifier.IsCaptcha(message, details));
    }

    [Theory]
    [InlineData("Субпрофили Avito 1: не удалось обновить список.")]
    [InlineData("Ошибка аккаунта Avito 1: timeout")]
    public void IsAutomationFailure_DetectsWorkerMessages(string message)
    {
        Assert.True(WorkerEventClassifier.IsAutomationFailure(message, "Warning"));
    }

    [Theory]
    [InlineData("Субпрофили Avito 1: парсер не обнаружил субпрофили в HTML модалки.")]
    [InlineData("Лимит AdsPower для Avito 1")]
    [InlineData("Rate limit AdsPower для Avito 1")]
    public void IsAutomationFailure_DoesNotCatchSpecificWorkerMessages(string message)
    {
        Assert.False(WorkerEventClassifier.IsAutomationFailure(message, "Warning"));
    }

    [Theory]
    [InlineData("Error", "Ошибка аккаунта Авито 32: Object reference not set to an instance of an object.", "critical")]
    [InlineData("Error", "Ошибка аккаунта Авито 31: Protocol error (Runtime.evaluate): Session closed.", "critical")]
    [InlineData(
        "Warning",
        "Субпрофиль «Кадровый Отдел10» · аккаунт «Авито 34» — проблема: net::ERR_INSUFFICIENT_RESOURCES at https://www.avito.ru/profile/pro/items",
        "critical")]
    [InlineData(
        "Warning",
        "Субпрофили Avito 15: требуется повторная авторизация в Avito — откройте браузер AdsPower и войдите (телефон/почта и пароль).",
        "high")]
    [InlineData(
        "Error",
        "Ошибка аккаунта Avito 15: Avito требует повторный вход на странице https://www.avito.ru/#login?next=%2Fprofile.",
        "high")]
    [InlineData(
        "Warning",
        "Субпрофиль «Контракт9» · аккаунт «Avito 14» — капча / блок IP: доступ ограничен: проблема с IP — откройте браузер AdsPower.",
        "high")]
    [InlineData("Warning", "Капча/firewall на аккаунте Avito 8", "high")]
    [InlineData("Error", "Ошибка аккаунта Avito 2: Timeout of 180000 ms exceeded", "medium")]
    [InlineData(
        "Warning",
        "AdsPower «Avito 13» · аккаунт «Avito 13» — проблема: ошибка на шаге «переключение субпрофиля».",
        "medium")]
    [InlineData(
        "Warning",
        "AdsPower «Avito19» · аккаунт «Avito19» — не переключился: не удалось перейти к «переключение субпрофиля»: открыта модалка",
        "medium")]
    [InlineData("Warning", "Профиль AdsPower занят для Авито 45", "low")]
    [InlineData(
        "Warning",
        "Субпрофиль «Кадровый дом4» · аккаунт «Авито 28» — проблема: net::ERR_ABORTED at https://www.avito.ru/profile/pro/items",
        "low")]
    public void InferSeverity_ClassifiesProductionPatterns(string level, string message, string expected)
    {
        Assert.Equal(expected, WorkerEventClassifier.InferSeverity(level, message));
    }

    [Fact]
    public void InferSeverity_UsesJsonDetails_ForAuthRequiredKind()
    {
        var details = """{"kind":"auth_required","url":"https://www.avito.ru/#login?next=%2Fprofile"}""";

        Assert.Equal(
            "high",
            WorkerEventClassifier.InferSeverity(
                "Error",
                "Ошибка аккаунта Avito 21: Avito требует повторный вход на странице https://www.avito.ru/#login?next=%2Fprofile.",
                details));
    }

    private static string BuildIssueMessage(string label, string detail) =>
        $"Субпрофиль «контракт РФ 1» · аккаунт «Avito 1» — {label}: {detail}";
}

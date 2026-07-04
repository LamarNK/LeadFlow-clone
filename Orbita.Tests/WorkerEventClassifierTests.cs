using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerEventClassifierTests
{
    [Theory]
    [InlineData("капча / блок IP", "captcha", "blocked")]
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

    private static string BuildIssueMessage(string label, string detail) =>
        $"Субпрофиль «контракт РФ 1» · аккаунт «Avito 1» — {label}: {detail}";
}
using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class WorkerEventClassificationMatrixTests
{
    private static readonly Guid WorkerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime OccurredAt = new(2026, 7, 2, 10, 7, 35, DateTimeKind.Utc);

    public static TheoryData<string, string, string, string, string> IssueCases => new()
    {
        {
            "капча / блок IP", "нужна проверка на странице откликов.", "Warning",
            "captcha", "blocked"
        },
        {
            "нужен вход", "требуется повторная авторизация.", "Warning",
            "auth", "auth"
        },
        {
            "не переключился", "не удалось переключить суб-профиль.", "Warning",
            "switch", "automation"
        },
        {
            "проблема", "не удалось перейти к откликам: сейчас неизвестная страница.", "Warning",
            "error", "automation"
        },
        {
            "ошибка парсинга", "ожидался JSON-объект откликов.", "Warning",
            "error", "parsing"
        },
        {
            "таймаут", "таймаут загрузки страницы.", "Warning",
            "error", "network"
        },
        {
            "лимит частоты AdsPower", "too many requests.", "Warning",
            "error", "api"
        },
        {
            "дневной лимит AdsPower", "daily open limit exceeded.", "Warning",
            "error", "api"
        }
    };

    [Theory]
    [MemberData(nameof(IssueCases))]
    public void FormattedIssue_ClassifiedConsistently_InEventsAndErrors(
        string label,
        string detail,
        string level,
        string expectedEventType,
        string expectedErrorType)
    {
        var message = $"Субпрофиль «контракт РФ 1» · аккаунт «Avito 1» — {label}: {detail}";
        var item = CreateEvent(level, message);

        var eventRow = EventsIndexBuilder.MapEvent(item);
        var errorRow = ErrorsIndexBuilder.MapEvent(item);

        Assert.Equal(expectedEventType, eventRow.EventType);
        Assert.Equal(expectedErrorType, errorRow.ErrorType);
        Assert.NotEqual("Новый отклик", eventRow.EventTypeLabel);
        Assert.NotEqual("Информация", eventRow.EventTypeLabel);
        if (expectedErrorType != "network")
            Assert.NotEqual("Ошибка сети", errorRow.ErrorTypeLabel);
        Assert.NotEqual("Неизвестная ошибка", errorRow.ErrorTypeLabel);
    }

    [Theory]
    [InlineData("Warning", "Субпрофили Avito 1: парсер не обнаружил субпрофили в HTML модалки.", "error", "parsing")]
    [InlineData("Warning", "Лимит AdsPower для Avito 1", "error", "api")]
    [InlineData("Warning", "Rate limit AdsPower для Avito 1", "error", "api")]
    [InlineData("Error", "Ошибка аккаунта Avito 1: сбой мониторинга.", "error", "automation")]
    public void WorkerMessages_Classified_InEventsAndErrors(
        string level,
        string message,
        string expectedEventType,
        string expectedErrorType)
    {
        var item = CreateEvent(level, message);

        var eventRow = EventsIndexBuilder.MapEvent(item);
        var errorRow = ErrorsIndexBuilder.MapEvent(item);

        Assert.Equal(expectedEventType, eventRow.EventType);
        Assert.Equal(expectedErrorType, errorRow.ErrorType);
    }

    [Fact]
    public void SuccessResponse_StillClassifiedAsNewResponse()
    {
        var item = CreateEvent("Success", "Новый отклик отправлен в CRM");

        var eventRow = EventsIndexBuilder.MapEvent(item);

        Assert.Equal("response", eventRow.EventType);
        Assert.Equal("Новый отклик", eventRow.EventTypeLabel);
    }

    private static WorkerEventListItem CreateEvent(string level, string message, string? details = null) =>
        new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            WorkerId,
            "WM1",
            AccountId,
            level,
            message,
            details,
            OccurredAt);
}
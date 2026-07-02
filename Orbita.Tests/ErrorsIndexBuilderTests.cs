using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class ErrorsIndexBuilderTests
{
    private static readonly Guid WorkerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime OccurredAt = new(2026, 7, 2, 10, 7, 35, DateTimeKind.Utc);

    [Fact]
    public void MapEvent_CaptchaWithUrlInDetails_ClassifiedAsBlocked_NotNetwork()
    {
        var message =
            "Субпрофиль «Лучшая работа в мире 8» · аккаунт «Avito 10» — капча / блок IP: нужна проверка на странице откликов.";
        var details = """
            {
              "attachmentId":"11111111-1111-1111-1111-111111111111",
              "kind":"hCaptcha",
              "url":"https://www.avito.ru/profile/candidates",
              "text":"нужна проверка на странице откликов.",
              "subProfileName":"Лучшая работа в мире 8"
            }
            """;

        var row = ErrorsIndexBuilder.MapEvent(CreateEvent("Warning", message, details));

        Assert.Equal("blocked", row.ErrorType);
        Assert.Equal("Блокировка аккаунта", row.ErrorTypeLabel);
    }

    [Fact]
    public void InferErrorType_UrlOnly_DoesNotClassifyAsNetwork()
    {
        var type = ErrorsIndexBuilder.InferErrorType(
            "Диагностика",
            """{"url":"https://www.avito.ru/profile/candidates"}""");

        Assert.NotEqual("network", type);
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

public sealed class WorkerEventDetailsParserTests
{
    [Fact]
    public void TryParseAttachmentId_ParsesJsonDetails()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var details = """{"attachmentId":"11111111-1111-1111-1111-111111111111","kind":"image-captcha"}""";

        var parsed = WorkerEventDetailsParser.TryParseAttachmentId(details);

        Assert.Equal(id, parsed);
    }

    [Fact]
    public void TryParseAttachmentId_ReturnsNull_ForPlainText()
    {
        Assert.Null(WorkerEventDetailsParser.TryParseAttachmentId("captcha :: url"));
    }

    [Fact]
    public void FormatForDisplay_ExtractsReadableText_FromJsonDetails()
    {
        var details = """
            {
              "attachmentId":"11111111-1111-1111-1111-111111111111",
              "kind":"parse-error",
              "url":"https://www.avito.ru/profile/candidates",
              "text":"Ожидался JSON-объект откликов",
              "subProfileName":"Служба 3"
            }
            """;

        var formatted = WorkerEventDetailsParser.FormatForDisplay(
            "Ошибка аккаунта Кабинет 1",
            details);

        Assert.Contains("Ожидался JSON-объект откликов", formatted);
        Assert.Contains("Служба 3", formatted);
        Assert.Contains("avito.ru/profile/candidates", formatted);
        Assert.DoesNotContain("attachmentId", formatted);
    }
}
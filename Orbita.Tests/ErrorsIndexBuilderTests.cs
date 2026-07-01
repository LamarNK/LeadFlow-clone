using Orbita.Contracts;

namespace Orbita.Tests;

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
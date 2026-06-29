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
}
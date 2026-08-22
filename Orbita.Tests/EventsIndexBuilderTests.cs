using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class EventsIndexBuilderTests
{
    private static readonly Guid WorkerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime OccurredAt = new(2026, 7, 2, 10, 7, 35, DateTimeKind.Utc);

    [Fact]
    public void MapEvent_CaptchaOnResponsesPage_ClassifiedAsCaptcha_NotResponse()
    {
        var message =
            "Субпрофиль «Лучшая работа в мире 8» · аккаунт «Avito 10» — капча / блок IP: нужна проверка на странице откликов.";

        var row = EventsIndexBuilder.MapEvent(CreateEvent("Warning", message));

        Assert.Equal("captcha", row.EventType);
        Assert.Equal("Капча", row.EventTypeLabel);
        Assert.Equal("warning", row.EventTypeTone);
    }

    [Fact]
    public void MapEvent_NewResponse_ClassifiedAsResponse()
    {
        var row = EventsIndexBuilder.MapEvent(CreateEvent("Success", "Новый отклик отправлен в CRM"));

        Assert.Equal("response", row.EventType);
        Assert.Equal("Новый отклик", row.EventTypeLabel);
    }

    [Fact]
    public void MapEvent_NavigationFailure_ClassifiedAsAutomationError_NotResponse()
    {
        var message =
            "Субпрофиль «контракт РФ 10» · аккаунт «Avito 1» — проблема: не удалось перейти к откликам: сейчас неизвестная страница.";

        var row = EventsIndexBuilder.MapEvent(CreateEvent("Warning", message));

        Assert.Equal("error", row.EventType);
        Assert.Equal("Сбой автоматизации", row.EventTypeLabel);
    }

    [Fact]
    public void MapEvent_SubProfileSwitchFailure_ClassifiedAsSwitch_NotInfo()
    {
        var message =
            "Субпрофиль «контракт РФ 1» · аккаунт «Avito 1» — не переключился: не удалось переключить суб-профиль.";

        var row = EventsIndexBuilder.MapEvent(CreateEvent("Warning", message));

        Assert.Equal("switch", row.EventType);
        Assert.Equal("Не переключился", row.EventTypeLabel);
    }

    [Fact]
    public void MapEvent_CaptchaKindInJsonDetails_ClassifiedAsCaptcha()
    {
        var details = """
            {
              "attachmentId":"11111111-1111-1111-1111-111111111111",
              "kind":"hCaptcha",
              "text":"нужна проверка на странице откликов.",
              "subProfileName":"Служба 3"
            }
            """;

        var row = EventsIndexBuilder.MapEvent(CreateEvent(
            "Warning",
            "Аккаунт «Avito 10» — требуется действие на странице откликов.",
            details));

        Assert.Equal("captcha", row.EventType);
        Assert.Equal("Капча", row.EventTypeLabel);
    }

    [Fact]
    public void MapEvent_IpBlock_IsClassifiedSeparately_AndCannotOpenCaptchaSolver()
    {
        const string message = "Субпрофиль «СлужбаРФ 6» · аккаунт «Avito 8 (2)» — блок IP: доступ ограничен: проблема с IP.";
        const string details = """{"kind":"firewall","url":"https://www.avito.ru/profile/candidates"}""";

        var row = EventsIndexBuilder.MapEvent(CreateEvent("Warning", message, details));

        Assert.Equal("ip_block", row.EventType);
        Assert.Equal("Блок IP", row.EventTypeLabel);
        Assert.False(row.CanSolveCaptcha);
    }

    private static WorkerEventListItem CreateEvent(string level, string message, string? details = null) =>
        new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            WorkerId,
            "WM1",
            AccountId,
            "user_01",
            level,
            message,
            details,
            OccurredAt);
}

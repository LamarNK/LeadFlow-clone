using LeadFlow.Core.Services.Avito.Session;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoPageObstacleProbeTests
{
    [Fact]
    public void Parse_CaptchaDialogOverWorkingList_ReturnsCaptcha()
    {
        // Модалка GeeTest v4 поверх РАБОЧЕЙ страницы откликов (список не пропал).
        const string json = """
            {"kind":"captcha","captchaKind":"geetest","url":"https://www.avito.ru/profile/candidates","title":"Работа: Отклики | Профиль | Авито","itemCount":24,"statusCount":24,"signals":["geetest-v4-mounted","captcha-dialog"]}
            """;

        var obstacle = AvitoPageObstacleProbe.Parse(json);

        Assert.Equal(AvitoPageObstacleKind.Captcha, obstacle.Kind);
        Assert.Equal("geetest", obstacle.CaptchaKind);
        Assert.Contains("captcha-dialog", obstacle.Signals ?? []);
        Assert.Equal("Работа: Отклики | Профиль | Авито", obstacle.Title);
    }

    [Fact]
    public void Parse_IpBlock_ReturnsIpBlocked()
    {
        const string json = """
            {"kind":"ipBlocked","captchaKind":null,"url":"https://www.avito.ru/","title":"Доступ ограничен","itemCount":0,"statusCount":0,"signals":["ip-block"]}
            """;

        var obstacle = AvitoPageObstacleProbe.Parse(json);

        Assert.Equal(AvitoPageObstacleKind.IpBlocked, obstacle.Kind);
    }

    [Theory]
    [InlineData("none", AvitoPageObstacleKind.None)]
    [InlineData("captcha", AvitoPageObstacleKind.Captcha)]
    [InlineData("ipBlocked", AvitoPageObstacleKind.IpBlocked)]
    [InlineData("loginRequired", AvitoPageObstacleKind.LoginRequired)]
    [InlineData("transientError", AvitoPageObstacleKind.TransientError)]
    [InlineData("manualAction", AvitoPageObstacleKind.ManualActionRequired)]
    [InlineData("something-new", AvitoPageObstacleKind.Unknown)]
    public void Parse_KindNames_MapToEnum(string kindName, AvitoPageObstacleKind expected)
    {
        var obstacle = AvitoPageObstacleProbe.Parse($$$"""{"kind":"{{{kindName}}}"}""");

        Assert.Equal(expected, obstacle.Kind);
    }

    [Fact]
    public void Parse_QuotedJsonString_Unwrapped()
    {
        var json = "{\"kind\":\"captcha\",\"captchaKind\":\"geetest\"}";

        var obstacle = AvitoPageObstacleProbe.Parse("\"" + json.Replace("\"", "\\\"") + "\"");

        Assert.Equal(AvitoPageObstacleKind.Captcha, obstacle.Kind);
        Assert.Equal("geetest", obstacle.CaptchaKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    public void Parse_Garbage_ReturnsUnknown(string? raw)
    {
        Assert.Equal(AvitoPageObstacleKind.Unknown, AvitoPageObstacleProbe.Parse(raw).Kind);
    }
}

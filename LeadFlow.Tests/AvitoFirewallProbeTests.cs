using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoFirewallProbeTests
{
    [Fact]
    public void TryParse_FirewallProbeJson_ReturnsDetection()
    {
        const string json = """
            {"blocked":true,"kind":"firewall","title":"Доступ ограничен: проблема с IP","url":"https://www.avito.ru/profile/candidates","itemCount":0}
            """;

        var detection = AvitoFirewallProbe.TryParse(json);

        Assert.NotNull(detection);
        Assert.Equal("firewall", detection!.Kind);
        Assert.Contains("avito.ru", detection.Url);
    }

    [Fact]
    public void TryParse_NotBlocked_ReturnsNull()
    {
        const string json = """{"blocked":false,"itemCount":5}""";

        Assert.Null(AvitoFirewallProbe.TryParse(json));
    }

    [Fact]
    public async Task ThrowIfBlockedAsync_SolveCallbackTrue_DoesNotThrow()
    {
        await AvitoFirewallProbe.ThrowIfBlockedAsync(
            (_, _) => Task.FromResult(
                """{"blocked":true,"kind":"geetest","url":"https://www.avito.ru/profile/candidates"}"""),
            null,
            "https://www.avito.ru/profile/candidates",
            CancellationToken.None,
            (_, _, _) => Task.FromResult(true));
    }

    [Fact]
    public async Task ThrowIfBlockedAsync_SolveCallbackFalse_Throws()
    {
        await Assert.ThrowsAsync<AvitoCaptchaDetectedException>(() =>
            AvitoFirewallProbe.ThrowIfBlockedAsync(
                (_, _) => Task.FromResult(
                    """{"blocked":true,"kind":"geetest","url":"https://www.avito.ru/profile/candidates"}"""),
                null,
                "https://www.avito.ru/profile/candidates",
                CancellationToken.None,
                (_, _, _) => Task.FromResult(false)));
    }
}

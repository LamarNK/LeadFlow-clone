using LeadFlow.Services.Avito;
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
}

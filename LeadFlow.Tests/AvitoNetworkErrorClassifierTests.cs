using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using PuppeteerSharp;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoNetworkErrorClassifierTests
{
    [Theory]
    [InlineData("net::ERR_PROXY_CONNECTION_FAILED at https://www.avito.ru", AvitoNetworkErrorKind.ProxyFailure)]
    [InlineData("net::ERR_TUNNEL_CONNECTION_FAILED", AvitoNetworkErrorKind.ProxyFailure)]
    [InlineData("net::ERR_SOCKS_CONNECTION_FAILED at http://x", AvitoNetworkErrorKind.ProxyFailure)]
    [InlineData("net::ERR_INTERNET_DISCONNECTED", AvitoNetworkErrorKind.NoInternet)]
    [InlineData("net::ERR_ADDRESS_UNREACHABLE", AvitoNetworkErrorKind.NoInternet)]
    [InlineData("net::ERR_NAME_NOT_RESOLVED", AvitoNetworkErrorKind.DnsFailure)]
    [InlineData("net::ERR_DNS_PROBE_FINISHED_NXDOMAIN", AvitoNetworkErrorKind.DnsFailure)]
    [InlineData("net::ERR_TIMED_OUT at https://www.avito.ru/dashboard", AvitoNetworkErrorKind.TransientNet)]
    [InlineData("net::ERR_CONNECTION_RESET", AvitoNetworkErrorKind.TransientNet)]
    [InlineData("net::ERR_EMPTY_RESPONSE", AvitoNetworkErrorKind.TransientNet)]
    [InlineData("ERR_CONNECTION_CLOSED", AvitoNetworkErrorKind.TransientNet)]
    public void ClassifyMessage_MapsChromeTokens(string message, AvitoNetworkErrorKind expected) =>
        Assert.Equal(expected, AvitoNetworkErrorClassifier.ClassifyMessage(message));

    [Fact]
    public void Classify_WalksInnerExceptionChain()
    {
        var navigation = new NavigationException("net::ERR_INTERNET_DISCONNECTED");
        var wrapped = new InvalidOperationException("навигация не удалась", navigation);

        Assert.Equal(AvitoNetworkErrorKind.NoInternet, AvitoNetworkErrorClassifier.Classify(wrapped));
    }

    [Theory]
    [InlineData("Execution Context was destroyed", AvitoNetworkErrorKind.None)]
    [InlineData("обычная ошибка автоматизации", AvitoNetworkErrorKind.None)]
    public void Classify_IgnoresNonNetworkMessages(string message, AvitoNetworkErrorKind expected) =>
        Assert.Equal(expected, AvitoNetworkErrorClassifier.ClassifyMessage(message));

    [Fact]
    public void TransientVsTerminal_Predicates()
    {
        var reset = new NavigationException("net::ERR_CONNECTION_RESET");
        var offline = new NavigationException("net::ERR_INTERNET_DISCONNECTED");
        var proxy = new NavigationException("net::ERR_PROXY_CONNECTION_FAILED");

        Assert.True(AvitoNetworkErrorClassifier.IsTransientRetryable(reset));
        Assert.False(AvitoNetworkErrorClassifier.IsTerminalNetworkError(reset));

        Assert.False(AvitoNetworkErrorClassifier.IsTransientRetryable(offline));
        Assert.True(AvitoNetworkErrorClassifier.IsTerminalNetworkError(offline));

        Assert.True(AvitoNetworkErrorClassifier.IsTerminalNetworkError(proxy));
    }

    [Fact]
    public void ClassifyHtml_DetectsChromeErrorPages()
    {
        Assert.Equal(
            AvitoNetworkErrorKind.ProxyFailure,
            AvitoNetworkErrorClassifier.ClassifyHtml("<html><body>Прокси-сервер не отвечает</body></html>"));
        Assert.Equal(
            AvitoNetworkErrorKind.NoInternet,
            AvitoNetworkErrorClassifier.ClassifyHtml("<html><body>Нет подключения к интернету</body></html>"));
        Assert.Equal(
            AvitoNetworkErrorKind.None,
            AvitoNetworkErrorClassifier.ClassifyHtml("<html><body>Отклики</body></html>"));
    }

    [Fact]
    public void BuildTerminalException_ProxyFailure_BecomesAdsPowerProxyFailure()
    {
        var source = new NavigationException("net::ERR_PROXY_CONNECTION_FAILED at https://www.avito.ru");

        var result = AvitoNetworkErrorClassifier.BuildTerminalException(source, "https://www.avito.ru");

        var proxy = Assert.IsType<AdsPowerProxyFailureException>(result);
        Assert.Contains("ERR_PROXY_CONNECTION_FAILED", proxy.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTerminalException_NoInternet_BecomesNetworkUnavailable()
    {
        var source = new NavigationException("net::ERR_INTERNET_DISCONNECTED");

        var result = AvitoNetworkErrorClassifier.BuildTerminalException(source, "https://www.avito.ru/dashboard");

        var network = Assert.IsType<AvitoNetworkUnavailableException>(result);
        Assert.Equal(AvitoNetworkErrorKind.NoInternet, network.Kind);
        Assert.Equal("ERR_INTERNET_DISCONNECTED", network.Token);
        Assert.Contains("нет доступа в интернет", network.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindNetworkToken_ExtractsTokenAfterPrefix()
    {
        var ex = new NavigationException("Navigation failed: net::ERR_NAME_NOT_RESOLVED at https://www.avito.ru");

        Assert.Equal("ERR_NAME_NOT_RESOLVED", AvitoNetworkErrorClassifier.FindNetworkToken(ex));
        Assert.Null(AvitoNetworkErrorClassifier.FindNetworkToken(new InvalidOperationException("нет токенов")));
    }
}

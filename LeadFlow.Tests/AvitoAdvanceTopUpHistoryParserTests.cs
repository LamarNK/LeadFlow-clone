using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAdvanceTopUpHistoryParserTests
{
    [Fact]
    public void FindMatchingAdvanceTopUp_DetectsPaidQr_WhenAdvanceBalanceIsStillStale()
    {
        const string html = """
            <div data-marker="operation">
              <article>
                <p>Внесение аванса</p>
                <p>11 сентября, 13:19</p>
              </article>
              <article><p>−<span>1&nbsp;946&nbsp;₽</span></p></article>
            </div>
            <div data-marker="operation">
              <article>
                <p>Внесение аванса</p>
                <p>11 сентября, 14:30</p>
              </article>
              <article><p>−<span>1&nbsp;946&nbsp;₽</span></p></article>
            </div>
            """;

        var capturedAtUtc = new DateTime(2026, 9, 11, 11, 40, 0, DateTimeKind.Utc);
        var paymentClaimedAtUtc = new DateTime(2026, 9, 11, 10, 19, 0, DateTimeKind.Utc);

        var match = AvitoAdvanceTopUpHistoryParser.FindMatchingAdvanceTopUp(
            html,
            requestedAmount: 1946m,
            paymentClaimedAtUtc,
            capturedAtUtc);

        Assert.NotNull(match);
        Assert.Equal(1946m, match.Amount);
        Assert.Equal(new DateTime(2026, 9, 11, 10, 19, 0, DateTimeKind.Utc), match.OccurredAtUtc);
    }

    [Fact]
    public void Parse_ReadsFourRealIncidentOperations()
    {
        const string html = """
            <button data-marker="tabs/tab(operations-history)">История операций</button>
            <div data-marker="operation"><p>Внесение аванса</p><p>11 сентября, 16:04</p><p>−<span>1&nbsp;946&nbsp;₽</span></p></div>
            <div data-marker="operation"><p>Внесение аванса</p><p>11 сентября, 15:34</p><p>−<span>1&nbsp;946&nbsp;₽</span></p></div>
            <div data-marker="operation"><p>Внесение аванса</p><p>11 сентября, 14:30</p><p>−<span>1&nbsp;946&nbsp;₽</span></p></div>
            <div data-marker="operation"><p>Внесение аванса</p><p>11 сентября, 13:19</p><p>−<span>1&nbsp;946&nbsp;₽</span></p></div>
            """;

        Assert.True(AvitoAdvanceTopUpHistoryParser.IsHistoryPage(html));
        var operations = AvitoAdvanceTopUpHistoryParser.Parse(
            html,
            new DateTime(2026, 9, 11, 14, 0, 0, DateTimeKind.Utc));

        Assert.Equal(4, operations.Count);
        Assert.All(operations, x => Assert.Equal(1946m, x.Amount));
    }
}

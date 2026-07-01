using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoBalanceParserTests
{
    [Fact]
    public void ParseAdvanceBalance_ValidHtml_ReturnsBalance()
    {
        var html = """
            <div class="styles-module-root-crqCe" data-marker="osp-sidebar/tools/money">
                <a href="/account/step1" class="Tiles-tile-IqmVC">
                    <div class="Tiles-card-H2qPF">
                        <article>
                            <div class="Tiles-info-OLT22">
                                <p class="styles-module-noAccent-kVktl">Кошелёк</p>
                                <h5 style="color:#000000">2&nbsp;₽</h5>
                                <p>Нет&nbsp;бонусов</p>
                            </div>
                        </article>
                    </div>
                </a>
                <a href="/tariff/cpa/profile" class="Tiles-tile-IqmVC">
                    <div class="Tiles-card-H2qPF">
                        <article>
                            <div class="Tiles-info-OLT22">
                                <p class="styles-module-noAccent-kVktl">Аванс</p>
                                <h5 style="color:#000000">0,00&nbsp;₽</h5>
                                <p>~ на 0 дней</p>
                            </div>
                        </article>
                    </div>
                </a>
            </div>
            """;

        var balance = AvitoBalanceParser.ParseAdvanceBalance(html);

        Assert.Equal(0.00m, balance);
    }

    [Fact]
    public void ParseAdvanceBalance_NonZeroBalance_ReturnsCorrectValue()
    {
        var html = """
            <div data-marker="osp-sidebar/tools/money">
                <a class="Tiles-tile-IqmVC">
                    <div class="Tiles-card-H2qPF">
                        <article>
                            <div class="Tiles-info-OLT22">
                                <p class="styles-module-noAccent-kVktl">Аванс</p>
                                <h5>1&nbsp;234,56&nbsp;₽</h5>
                                <p>~ на 30 дней</p>
                            </div>
                        </article>
                    </div>
                </a>
            </div>
            """;

        var balance = AvitoBalanceParser.ParseAdvanceBalance(html);

        Assert.Equal(1234.56m, balance);
    }

    [Fact]
    public void ParseAdvanceBalance_NoSidebar_ReturnsNull()
    {
        var html = "<html><body>no sidebar here</body></html>";
        var balance = AvitoBalanceParser.ParseAdvanceBalance(html);
        Assert.Null(balance);
    }

    [Fact]
    public void ParseAdvanceBalance_NoAdvanceTile_ReturnsNull()
    {
        var html = """
            <div data-marker="osp-sidebar/tools/money">
                <a class="Tiles-tile-IqmVC">
                    <div class="Tiles-card-H2qPF">
                        <article>
                            <div class="Tiles-info-OLT22">
                                <p class="styles-module-noAccent-kVktl">Кошелёк</p>
                                <h5>500&nbsp;₽</h5>
                            </div>
                        </article>
                    </div>
                </a>
            </div>
            """;

        var balance = AvitoBalanceParser.ParseAdvanceBalance(html);
        Assert.Null(balance);
    }

    [Fact]
    public void ParseAdvanceBalance_NullHtml_ReturnsNull()
    {
        Assert.Null(AvitoBalanceParser.ParseAdvanceBalance(null));
        Assert.Null(AvitoBalanceParser.ParseAdvanceBalance(""));
        Assert.Null(AvitoBalanceParser.ParseAdvanceBalance("   "));
    }

    [Fact]
    public void ParseAdvanceBalance_CurrentAvitoSidebar_ReturnsAdvanceNotWallet()
    {
        var html = """
            <div class="Tiles-root-N_U99" data-marker="osp-sidebar/tools/money">
                <a href="/account/step1" class="Tiles-tile-IqmVC">
                    <div class="Tiles-card-H2qPF">
                        <article>
                            <div class="Tiles-info-OLT22">
                                <p class="styles-module-noAccent-kVktl">Кошелёк</p>
                                <h5 style="color:#000000">0,00&nbsp;₽</h5>
                            </div>
                        </article>
                    </div>
                </a>
                <a href="/tariff/cpa/profile" class="Tiles-tile-IqmVC">
                    <div class="Tiles-card-H2qPF">
                        <article>
                            <div class="Tiles-info-OLT22">
                                <p class="styles-module-root-aoe_4 styles-module-size_xs-h7c0j styles-module-noAccent-kVktl">Аванс</p>
                                <h5 class="styles-module-root-ECS7y styles-module-size_xm-JQ25Y" style="color:#000000">885&nbsp;₽</h5>
                                <p>~ на 13 дней</p>
                            </div>
                        </article>
                    </div>
                </a>
            </div>
            """;

        var balance = AvitoBalanceParser.ParseAdvanceBalance(html);

        Assert.Equal(885m, balance);
    }
}

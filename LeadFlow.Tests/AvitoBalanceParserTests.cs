using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoBalanceParserTests
{
    [Fact]
    public void ParseMoneySidebar_HistoryPageSidebar_ReturnsAdvanceBalance()
    {
        const string html = """
            <div data-marker="osp-sidebar/tools/money">
              <a href="/account">
                <article><div>
                  <p>Кошелёк</p><h5>0,00&nbsp;₽</h5><p>Нет&nbsp;бонусов</p>
                </div></article>
              </a>
              <a href="/tariff/cpa/profile">
                <article><div>
                  <p>Аванс</p><h5>300&nbsp;₽</h5><p>~ на 1 день</p>
                </div></article>
              </a>
            </div>
            """;

        var money = AvitoBalanceParser.ParseMoneySidebar(html);

        Assert.NotNull(money);
        Assert.Equal(0m, money.WalletBalance);
        Assert.Equal(300m, money.AdvanceBalance);
    }

    [Fact]
    public void ParseMoneySidebar_BothTiles_ReturnsWalletAndAdvance()
    {
        var html = """
            <div class="Tiles-root-N_U99" data-marker="osp-sidebar/tools/money">
                <a href="/account/step1" class="Tiles-tile-IqmVC">
                    <div class="Tiles-card-H2qPF">
                        <article>
                            <div class="Tiles-info-OLT22">
                                <p class="styles-module-noAccent-kVktl">Кошелёк</p>
                                <h5 style="color:#000000">5&nbsp;₽</h5>
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
                                <h5 style="color:#000000">757&nbsp;₽</h5>
                                <p>~ на 9 дней</p>
                            </div>
                        </article>
                    </div>
                </a>
            </div>
            """;

        var money = AvitoBalanceParser.ParseMoneySidebar(html);

        Assert.NotNull(money);
        Assert.Equal(5m, money!.WalletBalance);
        Assert.Equal(757m, money.AdvanceBalance);
        Assert.Equal("~ на 9 дней", money.AdvanceDurationText);
    }

    [Fact]
    public void ParseMoneySidebar_WithRating_ReturnsWalletAdvanceAndReviews()
    {
        var html = """
            <div class="styles-profile-Wxgx5">
                <a data-marker="osp-sidebar/tools/stats/rating" href="/profile/rating">
                    <div>
                        <strong>5.0</strong>
                    </div>
                    <p>1&nbsp;отзыв</p>
                </a>
            </div>
            <div class="Tiles-root-N_U99" data-marker="osp-sidebar/tools/money">
                <a href="/account/step1" class="Tiles-tile-IqmVC">
                    <div class="Tiles-card-H2qPF">
                        <article>
                            <div class="Tiles-info-OLT22">
                                <p>Кошелёк</p>
                                <h5>5&nbsp;₽</h5>
                            </div>
                        </article>
                    </div>
                </a>
                <a href="/tariff/cpa/profile" class="Tiles-tile-IqmVC">
                    <div class="Tiles-card-H2qPF">
                        <article>
                            <div class="Tiles-info-OLT22">
                                <p>Аванс</p>
                                <h5>757&nbsp;₽</h5>
                                <p>~ на 9 дней</p>
                            </div>
                        </article>
                    </div>
                </a>
            </div>
            """;

        var sidebar = AvitoBalanceParser.ParseMoneySidebar(html);

        Assert.NotNull(sidebar);
        Assert.Equal(5m, sidebar!.WalletBalance);
        Assert.Equal(757m, sidebar.AdvanceBalance);
        Assert.Equal(5.0m, sidebar.Rating);
        Assert.Equal(1, sidebar.ReviewsCount);
        Assert.Equal("1 отзыв", sidebar.ReviewsText);
    }

    [Fact]
    public void ParseMoneySidebar_RatingOnly_ReturnsRatingWithoutMoney()
    {
        var html = """
            <a data-marker="osp-sidebar/tools/stats/rating" href="/profile/rating">
                <strong>4,8</strong>
                <p>12&nbsp;отзывов</p>
            </a>
            """;

        var sidebar = AvitoBalanceParser.ParseMoneySidebar(html);

        Assert.NotNull(sidebar);
        Assert.Null(sidebar!.WalletBalance);
        Assert.Null(sidebar.AdvanceBalance);
        Assert.Equal(4.8m, sidebar.Rating);
        Assert.Equal(12, sidebar.ReviewsCount);
        Assert.Equal("12 отзывов", sidebar.ReviewsText);
    }

    [Fact]
    public void ParseMoneySidebar_WalletOnlyBigTile_ReturnsWalletWithoutAdvance()
    {
        var html = """
            <div class="Tiles-root-N_U99" data-marker="osp-sidebar/tools/money">
                <a href="/account/step1" class="Tiles-big-tile-jvPh6">
                    <div class="Tiles-card-H2qPF">
                        <article>
                            <div class="Tiles-info-OLT22">
                                <p class="styles-module-noAccent-kVktl">Кошелёк</p>
                                <h5 style="color:#000000">0,00&nbsp;₽</h5>
                                <p>Нет&nbsp;бонусов</p>
                            </div>
                        </article>
                    </div>
                </a>
            </div>
            """;

        var money = AvitoBalanceParser.ParseMoneySidebar(html);

        Assert.NotNull(money);
        Assert.Equal(0m, money!.WalletBalance);
        Assert.Null(money.AdvanceBalance);
        Assert.Null(money.AdvanceDurationText);
    }

    [Fact]
    public void ParseMoneySidebar_NoMoneyMarker_ReturnsNull()
    {
        var html = """
            <div class="d0066f39fa375b74">
                <div data-marker="profile-sidebar-head/avatar" title="Контракт5"></div>
            </div>
            """;

        Assert.Null(AvitoBalanceParser.ParseMoneySidebar(html));
    }

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

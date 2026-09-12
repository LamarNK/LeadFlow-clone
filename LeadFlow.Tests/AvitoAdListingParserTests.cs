using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAdListingParserTests
{
    private readonly AvitoParserService _parser = new();

    [Theory]
    [InlineData("1 день на Авито", 1)]
    [InlineData("2 дня на Авито", 2)]
    [InlineData("5 дней на Авито", 5)]
    [InlineData("4 дня на Авито", 4)]
    public void TryParseAgeDays_ReadsRussianForms(string text, int expected)
    {
        Assert.Equal(expected, AvitoParserService.TryParseAgeDays(text));
    }

    [Fact]
    public void ParseProfilePage_ReadsIdTitleHrefAgeAndStatus()
    {
        const string html = """
            <div data-marker="item-snippet/8302808573">
                <a data-marker="view-link" href="/perm/vakansii/logist_v_ofis_8302808573">
                    <span>Логист в офис</span>
                </a>
                <div>Скрыто: не хватает денег на авансе</div>
                <div>4 дня на Авито</div>
            </div>
            """;

        var result = _parser.ParseProfilePage(html);
        var ad = Assert.Single(result.ActiveAds);
        Assert.Equal("8302808573", ad.Id);
        Assert.Equal("Логист в офис", ad.Title);
        Assert.Equal("https://www.avito.ru/perm/vakansii/logist_v_ofis_8302808573", ad.Url);
        Assert.True(ad.HasDaysOnAvito);
        Assert.Equal(4, ad.DaysOnAvito);
        Assert.Equal("Скрыто: не хватает денег на авансе", ad.Status);

        var card = Assert.Single(_parser.ToListCards(result));
        Assert.Equal("8302808573", card.AvitoItemId);
        Assert.Equal("https://www.avito.ru/perm/vakansii/logist_v_ofis_8302808573", card.Url);
        Assert.Equal(4, card.AgeDays);
        Assert.False(string.IsNullOrWhiteSpace(card.StatusText));
        Assert.Null(card.UrlParseError);
    }

    [Fact]
    public void ExtractListingUrl_UsesViewLinkEvenWhenCandidateAndChatLinksExist()
    {
        const string html = """
            <div data-marker="item-snippet/8302808573">
                <a href="/all/rezume?cv2Vacancy=8302808573">Подходящие кандидаты</a>
                <a href="/profile/messenger">Чаты</a>
                <a data-marker="view-link" target="_blank" rel="noopener noreferrer"
                   href="/perm/vakansii/logist_v_ofis_8302808573">
                    <span>Логист в офис</span>
                </a>
            </div>
            """;

        var url = AvitoParserService.ExtractListingUrl(html, out var error);

        Assert.Null(error);
        Assert.Equal("https://www.avito.ru/perm/vakansii/logist_v_ofis_8302808573", url);
    }

    [Fact]
    public void ExtractListingUrl_ResolvesProtocolRelativeHrefToHttps()
    {
        const string html = """
            <a data-marker="view-link" href="//www.avito.ru/kudrovo/vakansii/raznorabochiy_vahta_8126932974">x</a>
            """;

        var url = AvitoParserService.ExtractListingUrl(html, out var error);

        Assert.Null(error);
        Assert.Equal("https://www.avito.ru/kudrovo/vakansii/raznorabochiy_vahta_8126932974", url);
    }

    [Fact]
    public void ExtractListingUrl_ResolvesRelativeHrefToAbsolute()
    {
        const string html = """
            <a data-marker="view-link" href="/perm/vakansii/avitolog_v_ofis_8252186474">x</a>
            """;

        var url = AvitoParserService.ExtractListingUrl(html, out var error);

        Assert.Null(error);
        Assert.Equal("https://www.avito.ru/perm/vakansii/avitolog_v_ofis_8252186474", url);
    }

    [Fact]
    public void ExtractListingUrl_MissingViewLink_ReturnsEmptyWithoutThrowing()
    {
        const string html = """
            <div data-marker="item-snippet/8302808573">
                <a href="/all/rezume?cv2Vacancy=8302808573">Подходящие кандидаты</a>
                <span>Логист в офис</span>
            </div>
            """;

        var url = AvitoParserService.ExtractListingUrl(html, out var error);

        Assert.Equal(string.Empty, url);
        Assert.Equal("missing_view_link", error);

        var result = _parser.ParseProfilePage(html);
        var ad = Assert.Single(result.ActiveAds);
        Assert.Equal("8302808573", ad.Id);
        Assert.Equal(string.Empty, ad.ExplicitListingUrl);
        Assert.Equal("missing_view_link", ad.UrlParseError);
        Assert.Equal(string.Empty, Assert.Single(_parser.ToListCards(result)).Url);
    }

    [Fact]
    public void ParseProfilePage_RealActiveCard_ReadsMetricsStatusAndLeavesCityEmpty()
    {
        const string html = """
            <div data-marker="item-snippet/8302808573">
              <a data-marker="view-link" target="_blank" rel="noopener noreferrer"
                 href="/perm/vakansii/logist_v_ofis_8302808573">
                <span>Логист в офис</span>
              </a>
              <span class="styles-address-I7r1Q">ул. Куйбышева, 85А</span>
              <div>р-н Свердловский</div>
              <div role-marker="views">
                <span>208</span>
                <span>+24</span>
              </div>
              <div role-marker="contacts">
                <span>6</span>
                <span>+1</span>
              </div>
              <div role-marker="favorites">
                <span>19</span>
                <span>+5</span>
              </div>
              <div role-marker="viewsToContactsConversion">2,88 %</div>
              <span class="styles-status-name-zJgof">Скрыто: не хватает денег на авансе</span>
              <span>0 нет новых чатов</span>
              <div role-marker="offer/days-published">4 дня на Авито</div>
              <button>Редактировать</button>
              <button>Снять с публикации</button>
              <button>Поднять просмотры</button>
            </div>
            """;

        var ad = Assert.Single(_parser.ParseProfilePage(html).ActiveAds);

        Assert.Equal("8302808573", ad.Id);
        Assert.Equal("Логист в офис", ad.Title);
        Assert.Equal("https://www.avito.ru/perm/vakansii/logist_v_ofis_8302808573", ad.Url);
        Assert.Equal(208, ad.Views);
        Assert.Equal(6, ad.Contacts);
        Assert.Equal(19, ad.Favorites);
        Assert.Equal(4, ad.DaysOnAvito);
        Assert.Equal("Скрыто: не хватает денег на авансе", ad.Status);
        Assert.Equal(string.Empty, ad.City);
        Assert.Equal("ул. Куйбышева, 85А", ad.AddressText);
        Assert.Equal("р-н Свердловский", ad.DistrictText);

        var card = Assert.Single(_parser.ToListCards(_parser.ParseProfilePage(html)));
        Assert.Equal("Скрыто: не хватает денег на авансе", card.StatusText);
    }

    [Fact]
    public void ParseProfilePage_HiddenAdvanceStatus_StaysInActiveAds()
    {
        const string html = """
            <div data-marker="item-snippet/8302808573">
              <a data-marker="view-link" href="/perm/vakansii/logist_v_ofis_8302808573"><span>Логист в офис</span></a>
              <span>Скрыто: не хватает денег на авансе</span>
            </div>
            """;

        var ad = Assert.Single(_parser.ParseProfilePage(html).ActiveAds);
        Assert.Equal("Скрыто: не хватает денег на авансе", ad.Status);
    }

    [Fact]
    public void ParseProfilePage_BlockedStatus_IsExcludedFromActiveAds()
    {
        const string html = """
            <div data-marker="item-snippet/123">
              <a data-marker="view-link" href="/perm/vakansii/test_123"><span>Тест активной</span></a>
              <span>Заблокировано</span>
            </div>
            """;

        var result = _parser.ParseProfilePage(html);
        Assert.Empty(result.ActiveAds);
        Assert.Empty(result.BlockedAds);
    }

    [Fact]
    public void ParseProfilePage_StreetAddress_IsNotWrittenToCity()
    {
        const string html = """
            <div data-marker="item-snippet/8302808573">
              <a data-marker="view-link" href="/perm/vakansii/logist_v_ofis_8302808573"><span>Логист в офис</span></a>
              <span class="styles-address-I7r1Q">ул. Куйбышева, 85А</span>
            </div>
            """;

        var ad = Assert.Single(_parser.ParseProfilePage(html).ActiveAds);
        Assert.Equal(string.Empty, ad.City);
        Assert.Equal("ул. Куйбышева, 85А", ad.AddressText);
    }

    [Fact]
    public void ParseProfilePage_EmptyProWorkList_IsSuccessfulZeroAds()
    {
        const string html = """
            <div role="tablist" data-marker="profile-items-tab">
              <button data-marker="profile-items-tab/tab(active)">
                <span><span>Активные</span><span class="styles-module-counter-prLgf">0</span></span>
              </button>
            </div>
            """;

        var result = _parser.ParseProfilePage(html);

        Assert.True(result.ParseSuccess);
        Assert.Equal(AvitoProVacancyLayout.Supported, result.LayoutKind);
        Assert.Empty(result.ActiveAds);
        Assert.Equal(0, result.ItemSnippetMarkersFound);
        Assert.True(result.PageLoadedSuccessfully);
    }

    [Fact]
    public void ParseProfilePage_RegularProfileWithoutProMarkers_IsUnsupportedAndDoesNotParseCards()
    {
        const string html = """
            <div class="items-items-xyz">
              <div class="iva-item-root">
                <a href="/perm/transport/gazel_111">Газель</a>
                <span>4 дня</span>
                <span>Скрыто: не хватает денег на авансе</span>
              </div>
            </div>
            """;

        var result = _parser.ParseProfilePage(html);

        Assert.False(result.ParseSuccess);
        Assert.Equal(AvitoProVacancyLayout.UnsupportedProfileLayout, result.LayoutKind);
        Assert.Equal(AvitoProVacancyLayout.UnsupportedProfileLayout, result.ParseFailureReason);
        Assert.Empty(result.ActiveAds);
        Assert.Equal(0, result.ItemSnippetMarkersFound);
    }

    [Fact]
    public void ParseProfilePage_ViewLinkWithoutProSnippet_IsNotVacancyLayout()
    {
        const string html = """
            <a data-marker="view-link" href="/perm/transport/gazel_111">Газель</a>
            """;

        var result = _parser.ParseProfilePage(html);

        Assert.False(result.ParseSuccess);
        Assert.Equal(AvitoProVacancyLayout.UnsupportedProfileLayout, result.LayoutKind);
        Assert.Empty(result.ActiveAds);
    }

    [Fact]
    public void ParseItemDetailPage_ReadsIdTitleLifebarAndPublication()
    {
        const string html = """
            <h1 data-marker="item-view/title-info">Логист в офис</h1>
            <span data-marker="item-view/item-id">№ 8302808573, 9 сентября в 20:42</span>
            <span data-marker="item-lifebar">Осталось 27 дней</span>
            """;
        var captured = new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);

        var detail = _parser.ParseItemDetailPage(html, captured);

        Assert.True(detail.Success);
        Assert.Equal("8302808573", detail.AvitoItemId);
        Assert.Equal("Логист в офис", detail.Title);
        Assert.Equal(27, detail.RemainingDays);
        Assert.NotNull(detail.PublishedAtUtc);
    }

    [Fact]
    public void TryParseRemainingDays_ReadsLifeBar()
    {
        Assert.Equal(27, AvitoParserService.TryParseRemainingDays("Осталось 27 дней"));
        Assert.Equal(1, AvitoParserService.TryParseRemainingDays("Остался 1 день"));
    }
}

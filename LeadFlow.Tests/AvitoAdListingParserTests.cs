using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAdListingParserTests
{
    private readonly AvitoParserService _parser = new();

    [Fact]
    public void ParseUnpublishedTabPage_ExpiredCardWithoutDate_HasNoListExpiryError()
    {
        // Истёкшая карточка: Avito рендерит только «Истёк срок размещения», даты в разметке нет.
        const string html = """
            <div data-marker="item-snippet/8166386785">
              <a data-marker="view-link" href="/kapustin_yar/vakansii/slesar_vahta_s_pitaniem_i_prozhivaniem_8166386785">
                <span class="styles-title-UJzSB">Слесарь вахта с питанием и проживанием</span>
              </a>
              <span class="styles-status-name-zJgof styles-status-name_grey-zkGLh">Истёк срок размещения</span>
            </div>
            """;

        var card = Assert.Single(_parser.ToListCards(_parser.ParseUnpublishedTabPage(html)));

        Assert.Null(card.ExpiresAtUtc);
        Assert.Null(card.ExpiryParseError);
        Assert.Equal("Истёк срок размещения", card.StatusText);
    }

    [Fact]
    public void ParseProfilePage_ActiveCardWithoutExpiry_StillReportsParseError()
    {
        const string html = """
            <div data-marker="item-snippet/8302808573">
              <a data-marker="view-link" href="/perm/vakansii/logist_v_ofis_8302808573"><span>Логист в офис</span></a>
            </div>
            """;

        var card = Assert.Single(_parser.ToListCards(_parser.ParseProfilePage(html)));

        Assert.Null(card.ExpiresAtUtc);
        Assert.Equal("list_expiry_unparsed", card.ExpiryParseError);
    }

    [Fact]
    public void ParseProfilePage_InactiveStatusCardWithoutDate_HasNoListExpiryError()
    {
        const string html = """
            <div data-marker="item-snippet/8302808573">
              <a data-marker="view-link" href="/perm/vakansii/logist_v_ofis_8302808573"><span>Логист в офис</span></a>
              <span>Снято с публикации</span>
            </div>
            """;

        var card = Assert.Single(_parser.ToListCards(_parser.ParseProfilePage(html)));

        Assert.Null(card.ExpiresAtUtc);
        Assert.Null(card.ExpiryParseError);
        Assert.Equal("Снято с публикации", card.StatusText);
    }

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
    public void ParseProfilePage_LastLongCard_ReadsExpiryBeyondLegacySnippetLimit()
    {
        var filler = new string('x', 13_000);
        var html = $$"""
            <div data-marker="item-snippet/8461191164">
                <a data-marker="view-link" href="/sochi/vakansii/beregovaya_ohrana_8461191164">
                    <span>Береговая охрана</span>
                </a>
                <div>{{filler}}</div>
                <span class="styles-status-name">Активно</span>&nbsp;ещё 21 день — до 7 окт, 12:22
                <div>9 дней на Авито</div>
            </div>
            """;
        var captured = new DateTime(2026, 9, 16, 6, 0, 0, DateTimeKind.Utc);

        var card = Assert.Single(_parser.ToListCards(_parser.ParseProfilePage(html, capturedAtUtc: captured)));

        Assert.Equal(21, card.RemainingDays);
        Assert.Null(card.ExpiryParseError);
        Assert.NotNull(card.ExpiresAtUtc);
    }

    [Fact]
    public void ParseProfilePage_ReadsPresentationFieldsForListingsUi()
    {
        var html = """
            <div data-marker="item-snippet/8285468940">
                <div style="background-image: url(&quot;//60.img.avito.st/image/test&quot;);"></div>
                <a data-marker="view-link" href="/volginskiy/vakansii/mehanik_8285468940"><span>Механик</span></a>
                <span>от 230 000 ₽</span><span class="styles-address">пос. Вольгинский</span>
                <div role-marker="views"><span>42</span></div>
                <div role-marker="contacts"><span>7</span></div>
                <div role-marker="favorites"><span>3</span></div>
                <span>Активно</span> ещё 21 день — до 7 окт, 12:22
            </div>
            """;

        var card = Assert.Single(_parser.ToListCards(_parser.ParseProfilePage(
            html,
            capturedAtUtc: new DateTime(2026, 9, 16, 6, 0, 0, DateTimeKind.Utc))));

        Assert.Equal("https://60.img.avito.st/image/test", card.ImageUrl);
        Assert.Equal("от 230 000 ₽", card.Salary);
        Assert.Equal(42, card.Views);
        Assert.Equal(7, card.Contacts);
        Assert.Equal(3, card.Favorites);
    }

    [Fact]
    public void ParseProfilePage_ReadsExactExpiryFromActiveCard_WithoutOpeningDetailPage()
    {
        const string html = """
            <div data-marker="item-snippet/8285468940">
              <a data-marker="view-link" href="/volginskiy/vakansii/mehanik_8285468940">
                <span>Механик вахта</span>
              </a>
              <span class="styles-status-name-zJgof">Активно</span>&nbsp;ещё 13 дней — до 26 сен, 10:23
              <div role-marker="offer/days-published">17 дней на Авито</div>
            </div>
            """;
        var captured = new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

        var card = Assert.Single(_parser.ToListCards(_parser.ParseProfilePage(html, capturedAtUtc: captured)));
        var expiresLocal = Assert.IsType<DateTime>(card.ExpiresAtUtc);
        expiresLocal = AvitoAdBusinessTime.ToLocal(expiresLocal);

        Assert.Equal(2026, expiresLocal.Year);
        Assert.Equal(9, expiresLocal.Month);
        Assert.Equal(26, expiresLocal.Day);
        Assert.Equal(10, expiresLocal.Hour);
        Assert.Equal(23, expiresLocal.Minute);
        Assert.Null(card.ExpiryParseError);
    }

    [Fact]
    public void ParseProfilePage_ReadsOctoberExpiryFromCapturedAvitoCard()
    {
        const string html = """
            <div data-marker="item-snippet/8312560791">
              <a data-marker="view-link" href="https://www.avito.ru/novoiivanovskoe/vakansii/mehanik_8312560791">
                <span>Механик вахта в Воронеж с проживанием</span>
              </a>
              <span class="styles-status-name-zJgof">Активно</span>&nbsp;ещё 18 дней — до 1 окт, 17:33
              <div role-marker="offer/days-published">13 дней на Авито</div>
            </div>
            """;
        var captured = new DateTime(2026, 9, 13, 1, 5, 54, DateTimeKind.Utc);

        var card = Assert.Single(_parser.ToListCards(_parser.ParseProfilePage(html, capturedAtUtc: captured)));
        var expiresLocal = Assert.IsType<DateTime>(card.ExpiresAtUtc);
        expiresLocal = AvitoAdBusinessTime.ToLocal(expiresLocal);

        Assert.Equal(2026, expiresLocal.Year);
        Assert.Equal(10, expiresLocal.Month);
        Assert.Equal(1, expiresLocal.Day);
        Assert.Equal(17, expiresLocal.Hour);
        Assert.Equal(33, expiresLocal.Minute);
        Assert.Equal(18, card.RemainingDays);
        Assert.Null(card.ExpiryParseError);
    }

    [Theory]
    [InlineData("до 1 янв, 10:00", 1)]
    [InlineData("до 1 фев, 10:00", 2)]
    [InlineData("до 1 мар, 10:00", 3)]
    [InlineData("до 1 апр, 10:00", 4)]
    [InlineData("до 1 мая, 10:00", 5)]
    [InlineData("до 1 июн, 10:00", 6)]
    [InlineData("до 1 июл, 10:00", 7)]
    [InlineData("до 1 авг, 10:00", 8)]
    [InlineData("до 1 сен, 10:00", 9)]
    [InlineData("до 1 окт, 10:00", 10)]
    [InlineData("до 1 ноя, 10:00", 11)]
    [InlineData("до 1 дек, 10:00", 12)]
    public void TryParseActiveListExpiry_ReadsAllRussianShortMonths(string text, int expectedMonth)
    {
        var captured = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var ok = AvitoParserService.TryParseActiveListExpiry(
            $"Активно ещё 13 дней — {text}",
            captured,
            out var expiresAtUtc,
            out var remainingDays,
            out var error);

        Assert.True(ok);
        Assert.Equal(expectedMonth, AvitoAdBusinessTime.ToLocal(expiresAtUtc).Month);
        Assert.Equal(13, remainingDays);
        Assert.Null(error);
    }

    [Fact]
    public void TryParseActiveListExpiry_DecodesNbspAndAcceptsEnDash()
    {
        var captured = new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

        var ok = AvitoParserService.TryParseActiveListExpiry(
            "Активно&nbsp;ещё&nbsp;18&nbsp;дней&nbsp;–&nbsp;до&nbsp;1&nbsp;октября,&nbsp;17:33",
            captured,
            out var expiresAtUtc,
            out var remainingDays,
            out var error);

        var expiresLocal = AvitoAdBusinessTime.ToLocal(expiresAtUtc);
        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 10, 1, 17, 33, 0), expiresLocal);
        Assert.Equal(18, remainingDays);
        Assert.Null(error);
    }

    [Fact]
    public void ExtractItemSnippetHtml_ReturnsLimitedCardFragment()
    {
        const string html = """
            <div data-marker="item-snippet/8312560791">
              <span>Активно ещё 18 дней — до 1 окт, 17:33</span>
            </div>
            """;

        var fragment = AvitoParserService.ExtractItemSnippetHtml(html, "8312560791", maxLength: 40);

        Assert.Contains("item-snippet/8312560791", fragment);
        Assert.Contains("truncated", fragment);
        Assert.Equal(
            "Активно ещё 18 дней — до 1 окт, 17:33",
            AvitoParserService.ExtractActiveListStatusLine(html));
        Assert.Equal(
            "ещё 18 дней — до 1 окт, 17:33",
            AvitoParserService.ExtractActiveListExpiryText(html));
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

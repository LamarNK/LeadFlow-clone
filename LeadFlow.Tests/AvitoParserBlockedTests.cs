using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

/// <summary>
/// Тесты вкладки «С ошибками» (<c>tab(rejected)</c>) — снимок HTML взят из реального ответа Авито,
/// где одна заблокированная вакансия с подписью «Заблокировано, удалится навсегда 22 мая в 20:50».
/// </summary>
public sealed class AvitoParserBlockedTests
{
    private readonly AvitoParserService _parser = new();

    [Fact]
    public void ParseProfilePage_TabsRow_ReadsActiveAndRejectedAndDraftsCounters()
    {
        // Реальная разметка панели вкладок с пользовательского снимка: «Активные 49 / С ошибками 1 / Черновики 4».
        // Это ровно тот случай, при котором мониторинг должен открывать вкладку rejected — иначе блок не парсится.
        const string html = """
            <div role="tablist" data-marker="profile-items-tab">
              <button data-marker="profile-items-tab/tab(active)">
                <span><span>Активные</span><span class="styles-module-counter-prLgf styles-module-counter_size-l-drhmu">49</span></span>
              </button>
              <button data-marker="profile-items-tab/tab(rejected)">
                <span><span>С ошибками</span><span class="styles-module-counter-prLgf styles-module-counter_size-l-drhmu">1</span></span>
              </button>
              <button data-marker="profile-items-tab/tab(inactive)">
                <span><span>Неопубликованные</span><span class="styles-module-counter-prLgf styles-module-counter_disabled-bJRBA">0</span></span>
              </button>
              <button data-marker="profile-items-tab/tab(drafts)">
                <span><span>Черновики</span><span class="styles-module-counter-prLgf styles-module-counter_size-l-drhmu">4</span></span>
              </button>
            </div>
            """;

        var result = _parser.ParseProfilePage(html);

        Assert.Equal(49, result.ActiveCount);
        // Именно по этому счётчику мониторинг решает «открывать ли вкладку rejected».
        Assert.Equal(1, result.BlockedCount);
        Assert.Equal(4, result.DraftsCount);
    }

    [Fact]
    public void ParseBlockedTabPage_SingleRejectedJob_ReturnsOneAdWithStatusAndDeleteDate()
    {
        // Минимально-достаточная разметка вкладки «С ошибками»: одна вакансия в /rabota/-категории,
        // блок статуса со span class=styles-status-name_red-* и хвостом «, удалится навсегда ...».
        const string html = """
            <div class="style-scrollable-content-container-iZOic">
              <div role-marker="offer">
                <div class="style-layout-b2g0K" data-marker="item-snippet/8095860333">
                  <div class="styles-snippet-IdzHk">
                    <a data-marker="view-link" href="//www.avito.ru/solikamsk/vakansii/raznorabochiy_vahta_8095860333">
                      <span class="styles-title-UJzSB">Разнорабочий вахта</span>
                    </a>
                    <span class="styles-address-I7r1Q">Пермский край, Соликамск</span>
                  </div>
                  <div class="styles-status-wrapper-Jlm37">
                    <span class="styles-status-EGgGM">
                      <div class="styles-catch-block-EZLET">
                        <span class="styles-status-name-zJgof styles-status-name_red-f6Cnh">Заблокировано</span>
                      </div>, удалится навсегда 22 мая в 20:50
                    </span>
                  </div>
                </div>
              </div>
            </div>
            """;

        var blocked = _parser.ParseBlockedTabPage(html);

        var ad = Assert.Single(blocked);
        Assert.Equal("8095860333", ad.Id);
        Assert.Equal("Разнорабочий вахта", ad.Title);
        Assert.Equal("Пермский край, Соликамск", ad.City);
        Assert.Equal("Заблокировано", ad.Status);
        Assert.Equal("удалится навсегда 22 мая в 20:50", ad.DeleteDate);
        // Полный URL нужен, чтобы из дашборда можно было открыть страницу заблокированного объявления.
        Assert.Equal("https://www.avito.ru/solikamsk/vakansii/raznorabochiy_vahta_8095860333", ad.Url);
    }

    [Fact]
    public void ParseBlockedTabPage_NonJobListing_IsSkipped()
    {
        // Снимок не из раздела /rabota/ — фильтр по «вакансиям» отбрасывает.
        const string html = """
            <div role-marker="offer">
              <div class="style-layout-b2g0K" data-marker="item-snippet/777">
                <a data-marker="view-link" href="//www.avito.ru/moscow/audio_video/televizor_777">
                  <span class="styles-title-UJzSB">Телевизор</span>
                </a>
                <span class="styles-status-name-zJgof styles-status-name_red-f6Cnh">Заблокировано</span>
              </div>
            </div>
            """;

        var blocked = _parser.ParseBlockedTabPage(html);

        Assert.Empty(blocked);
    }

    [Fact]
    public void ParseBlockedTabPage_MissingDeleteDate_StatusStillExtracted()
    {
        // Иногда «удалится навсегда» отсутствует — сам статус всё равно должен подняться.
        const string html = """
            <div role-marker="offer">
              <div class="style-layout-b2g0K" data-marker="item-snippet/1">
                <a data-marker="view-link" href="//www.avito.ru/perm/vakansii/test_1">
                  <span class="styles-title-UJzSB">Тест</span>
                </a>
                <div class="styles-status-wrapper-Jlm37">
                  <span class="styles-status-EGgGM">
                    <span class="styles-status-name-zJgof styles-status-name_red-f6Cnh">Заблокировано</span>
                  </span>
                </div>
              </div>
            </div>
            """;

        var ad = Assert.Single(_parser.ParseBlockedTabPage(html));
        Assert.Equal("Заблокировано", ad.Status);
        Assert.Equal(string.Empty, ad.DeleteDate);
    }

    [Fact]
    public void ParseBlockedTabPage_EmptyOrNullHtml_ReturnsEmpty()
    {
        Assert.Empty(_parser.ParseBlockedTabPage(string.Empty));
        Assert.Empty(_parser.ParseBlockedTabPage(null!));
    }

    [Fact]
    public void ParseProfilePage_VacancyCardWithResumeLink_UsesPublicListingUrl()
    {
        // Реальный снимок Pro: рядом с заголовком есть data-marker="view-link" на карточку,
        // а ниже — ссылка «Подходящие кандидаты» (cvlink) на /all/rezume?cv2Vacancy=...
        const string html = """
            <div data-marker="item-snippet/8126932974">
              <a data-marker="view-link" target="_blank" rel="noopener noreferrer"
                 href="//www.avito.ru/kudrovo/vakansii/raznorabochiy_vahta_8126932974"
                 class="x">
                <span class="styles-title-UJzSB">Разнорабочий вахта</span>
              </a>
              <a target="_blank" data-marker="cvlink"
                 href="https://www.avito.ru/all/rezume?cv2Vacancy=8126932974&amp;fromPage=rec_cv2vac_my_items">Подходящие кандидаты</a>
            </div>
            """;

        var ad = Assert.Single(_parser.ParseProfilePage(html).ActiveAds);

        Assert.Equal("8126932974", ad.Id);
        Assert.Equal(
            "https://www.avito.ru/kudrovo/vakansii/raznorabochiy_vahta_8126932974",
            ad.Url);
    }

    [Fact]
    public void ParseProfilePage_ViewLinkNotOnAnchor_DoesNotInventUrlFromOtherLinks()
    {
        const string html = """
            <div data-marker="item-snippet/8126932974">
              <div data-marker="view-link">
                <a href="//www.avito.ru/kudrovo/vakansii/raznorabochiy_vahta_8126932974">
                  <span class="styles-title-UJzSB">Разнорабочий вахта</span>
                </a>
              </div>
            </div>
            """;

        var ad = Assert.Single(_parser.ParseProfilePage(html).ActiveAds);

        Assert.Equal(string.Empty, ad.ExplicitListingUrl);
        Assert.Equal("missing_view_link", ad.UrlParseError);
    }

    [Fact]
    public void ParseProfilePage_BlockedSnippetOnActiveTab_StillSkipped()
    {
        // Если кейс «осталась заблокированная карточка на активной вкладке» — её
        // в ActiveAds НЕ добавляем (это работа ParseBlockedTabPage).
        const string html = """
            <div role-marker="offer">
              <div class="style-layout-b2g0K" data-marker="item-snippet/123">
                <a data-marker="view-link" href="//www.avito.ru/perm/vakansii/test_123">
                  <span class="styles-title-UJzSB">Тест активной</span>
                </a>
                <span class="styles-status-name-zJgof styles-status-name_red-f6Cnh">Заблокировано</span>
              </div>
            </div>
            """;

        var result = _parser.ParseProfilePage(html);

        Assert.Empty(result.ActiveAds);
        Assert.Empty(result.BlockedAds);
    }

    [Fact]
    public void ParseProfilePage_ReadsInactiveCounter()
    {
        const string html = """
            <button data-marker="profile-items-tab/tab(active)"><span class="styles-module-counter-prLgf">0</span></button>
            <button data-marker="profile-items-tab/tab(inactive)"><span class="styles-module-counter-prLgf">4</span></button>
            <button data-marker="profile-items-tab/tab(rejected)"><span class="styles-module-counter-prLgf">0</span></button>
            """;

        var result = _parser.ParseProfilePage(html);

        Assert.Equal(4, result.UnpublishedCount);
    }

    [Fact]
    public void ParseUnpublishedTabPage_ReadsPublishCapabilityAndReason()
    {
        const string html = """
            <div data-marker="item-snippet/8140037674">
              <a data-marker="view-link" href="/gubkinskiy/vakansii/test_8140037674">
                <span class="styles-title-UJzSB">Охранник</span>
              </a>
              <span class="styles-status-name-zJgof">Истёк срок размещения</span>
              <button data-marker="publish-action">Опубликовать</button>
            </div>
            """;

        var ad = Assert.Single(_parser.ParseUnpublishedTabPage(html));

        Assert.Equal("8140037674", ad.Id);
        Assert.Equal("inactive", ad.SourceTab);
        Assert.True(ad.CanPublish);
        Assert.Equal("Истёк срок размещения", ad.Status);
    }

    [Fact]
    public void ParseUnpublishedTabPage_DoesNotDropCardWhenListingUrlIsMalformed()
    {
        const string html = """
            <div data-marker="item-snippet/8140037675">
              <div data-marker="view-link"><span class="styles-title-UJzSB">Оператор</span></div>
              <span class="styles-status-name-zJgof">Ошибки автопубликации</span>
            </div>
            """;

        var ad = Assert.Single(_parser.ParseUnpublishedTabPage(html));

        Assert.Equal("8140037675", ad.Id);
        Assert.Equal("missing_view_link", ad.UrlParseError);
        Assert.Equal("Ошибки автопубликации", ad.Status);
    }

    [Fact]
    public void ParseBlockedTabPage_ExtractsExplicitErrorReason()
    {
        const string html = """
            <div data-marker="item-snippet/1">
              <a data-marker="view-link" href="/perm/vakansii/test_1"><span>Тест</span></a>
              <div>Ошибка публикации: Не заполнено поле «График работы».</div>
              <span class="styles-status-name-zJgof">Отклонено</span>
            </div>
            """;

        var ad = Assert.Single(_parser.ParseBlockedTabPage(html));

        Assert.Equal("rejected", ad.SourceTab);
        Assert.Contains("Не заполнено поле", ad.ErrorReason, StringComparison.OrdinalIgnoreCase);
    }
}
